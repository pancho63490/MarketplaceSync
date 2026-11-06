using System.Net.Http.Headers;
using Hangfire;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MarketplaceSync.Web.Services.Tenancy;

namespace MarketplaceSync.Web.Controllers
{
    [Authorize]
    public class MercadoLibreController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly AppDbContext _context;
        private readonly IMercadoLibreService _mercadoLibreService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOrganizationContext _organizationContext;

        public MercadoLibreController(
            IConfiguration configuration,
            AppDbContext context,
            IMercadoLibreService mercadoLibreService,
            IHttpClientFactory httpClientFactory,
            IOrganizationContext organizationContext)
        {
            _configuration = configuration;
            _context = context;
            _mercadoLibreService = mercadoLibreService;
            _httpClientFactory = httpClientFactory;
            _organizationContext = organizationContext;
        }

        // =====================================================
        // STATUS
        // =====================================================

        [HttpGet]
        public async Task<IActionResult> Status()
        {
            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

            var accounts = await _context.MarketplaceAccounts
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre")
                .OrderByDescending(x => x.ConnectedAt)
                .Select(x => new MarketplaceAccountStatusViewModel
                {
                    Id = x.Id,
                    Marketplace = x.Marketplace,
                    DisplayName = x.DisplayName ?? "Cuenta sin nombre",
                    ExternalAccountId = x.ExternalAccountId,
                    Status = x.Status,
                    ConnectedAt = x.ConnectedAt,
                    DisconnectedAt = x.DisconnectedAt,
                    HasActiveToken = x.Tokens.Any(t => t.IsActive),
                    TokenExpiresAt = x.Tokens
                        .Where(t => t.IsActive)
                        .OrderByDescending(t => t.UpdatedAt)
                        .Select(t => t.ExpiresAt)
                        .FirstOrDefault(),
                    TokenUpdatedAt = x.Tokens
                        .Where(t => t.IsActive)
                        .OrderByDescending(t => t.UpdatedAt)
                        .Select(t => t.UpdatedAt)
                        .FirstOrDefault()
                })
                .ToListAsync();

            return View(accounts);
        }

        // =====================================================
        // CONNECT
        // =====================================================

        [HttpGet]
        public IActionResult Connect()
        {
            var clientId =
                _configuration["MercadoLibre:ClientId"];

            var redirectUri =
                _configuration["MercadoLibre:RedirectUri"];

            var authUrl =
                _configuration["MercadoLibre:AuthUrl"];

            if (string.IsNullOrWhiteSpace(clientId))
                return BadRequest(
                    "Missing MercadoLibre ClientId.");

            if (string.IsNullOrWhiteSpace(redirectUri))
                return BadRequest(
                    "Missing MercadoLibre RedirectUri.");

            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var callbackUri) ||
                callbackUri.Scheme != Uri.UriSchemeHttps)
            {
                return BadRequest("MercadoLibre RedirectUri must be an absolute HTTPS URL.");
            }

            if (string.IsNullOrWhiteSpace(authUrl))
            {
                authUrl =
                    "https://auth.mercadolibre.com.mx/authorization";
            }

            var state =
                Guid.NewGuid().ToString("N");

            HttpContext.Session.SetString(
                "ML_OAUTH_STATE",
                state);

            var url =
                $"{authUrl}" +
                $"?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&state={Uri.EscapeDataString(state)}";

            return Redirect(url);
        }

        // =====================================================
        // CALLBACK
        // =====================================================

        [HttpGet]
        public async Task<IActionResult> Callback(
            string? code,
            string? state,
            string? error,
            string? error_description)
        {
            var appUserId =
                User.FindFirstValue(
                    ClaimTypes.NameIdentifier);
            var appUserName = User.Identity?.Name;
            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

            if (string.IsNullOrWhiteSpace(appUserId) || string.IsNullOrWhiteSpace(appUserName))
            {
                TempData["Error"] = "Debes iniciar sesión para conectar Mercado Libre.";
                return RedirectToAction("Login", "Account");
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                TempData["Error"] =
                    $"Mercado Libre error: {error} {error_description}";

                return RedirectToAction(nameof(Status));
            }

            var expectedState = HttpContext.Session.GetString("ML_OAUTH_STATE");
            HttpContext.Session.Remove("ML_OAUTH_STATE");

            if (string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(expectedState) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                TempData["Error"] = "La validación de seguridad de Mercado Libre expiró o no es válida.";
                return RedirectToAction(nameof(Status));
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                TempData["Error"] =
                    "Mercado Libre did not return authorization code.";

                return RedirectToAction(nameof(Status));
            }

            var clientId =
                _configuration["MercadoLibre:ClientId"];

            var clientSecret =
                _configuration["MercadoLibre:ClientSecret"];

            var redirectUri =
                _configuration["MercadoLibre:RedirectUri"];

            var tokenUrl =
                _configuration["MercadoLibre:TokenUrl"];

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(redirectUri))
            {
                TempData["Error"] = "La integración de Mercado Libre no está configurada completamente.";
                return RedirectToAction(nameof(Status));
            }

            if (string.IsNullOrWhiteSpace(tokenUrl))
            {
                tokenUrl =
                    "https://api.mercadolibre.com/oauth/token";
            }

            var client =
                _httpClientFactory.CreateClient();

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    tokenUrl);

            request.Content =
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        { "grant_type", "authorization_code" },
                        { "client_id", clientId! },
                        { "client_secret", clientSecret! },
                        { "code", code },
                        { "redirect_uri", redirectUri! }
                    });

            using var response =
                await client.SendAsync(request);

            var content =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                TempData["Error"] =
                    $"Mercado Libre token error: {content}";

                return RedirectToAction(nameof(Status));
            }

            var tokenResponse =
                JsonSerializer.Deserialize<MercadoLibreTokenResponse>(
                    content,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (tokenResponse == null ||
                string.IsNullOrWhiteSpace(
                    tokenResponse.AccessToken))
            {
                TempData["Error"] =
                    "Invalid Mercado Libre token response.";

                return RedirectToAction(nameof(Status));
            }

            var nickname =
                await _mercadoLibreService
                    .GetNicknameAsync(
                        tokenResponse.AccessToken);

            var expiresAt =
                DateTime.UtcNow.AddSeconds(
                    tokenResponse.ExpiresIn);

            var externalAccountId = tokenResponse.UserId.ToString();
            var marketplaceAccount = await _context.MarketplaceAccounts
                .FirstOrDefaultAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.Marketplace == "MercadoLibre" &&
                    x.ExternalAccountId == externalAccountId);

            if (marketplaceAccount == null)
            {
                marketplaceAccount = new MarketplaceAccount
                {
                    OrganizationId = organizationId,
                    Marketplace = "MercadoLibre",
                    ExternalAccountId = externalAccountId,
                    ConnectedByUserId = appUserId,
                    ConnectedAt = DateTime.UtcNow
                };
                _context.MarketplaceAccounts.Add(marketplaceAccount);
            }

            marketplaceAccount.DisplayName = nickname;
            marketplaceAccount.Status = "Connected";
            marketplaceAccount.DisconnectedAt = null;

            var existingToken =
                await _context.MercadoLibreTokens
                    .FirstOrDefaultAsync(x =>
                        x.UserId ==
                        tokenResponse.UserId.ToString()
                        &&
                        x.OrganizationId == organizationId);

            if (existingToken == null)
            {
                existingToken =
                    new MercadoLibreToken
                    {
                        OrganizationId = organizationId,
                        MarketplaceAccount = marketplaceAccount,
                        ConnectedByUserId = appUserId,
                        AppUserName = appUserName,
                        UserId =
                            tokenResponse.UserId.ToString(),
                        Nickname = nickname,
                        AccessToken =
                            tokenResponse.AccessToken,
                        RefreshToken =
                            tokenResponse.RefreshToken,
                        TokenType =
                            tokenResponse.TokenType,
                        Scope =
                            tokenResponse.Scope,
                        ExpiresIn =
                            tokenResponse.ExpiresIn,
                        ExpiresAt = expiresAt,
                        CreatedAt =
                            DateTime.UtcNow,
                        UpdatedAt =
                            DateTime.UtcNow,
                        IsActive = true
                    };

                _context.MercadoLibreTokens
                    .Add(existingToken);
            }
            else
            {
                existingToken.MarketplaceAccount = marketplaceAccount;
                existingToken.Nickname =
                    nickname;

                existingToken.AccessToken =
                    tokenResponse.AccessToken;

                existingToken.RefreshToken =
                    tokenResponse.RefreshToken;

                existingToken.TokenType =
                    tokenResponse.TokenType;

                existingToken.Scope =
                    tokenResponse.Scope;

                existingToken.ExpiresIn =
                    tokenResponse.ExpiresIn;

                existingToken.ExpiresAt =
                    expiresAt;

                existingToken.UpdatedAt =
                    DateTime.UtcNow;

                existingToken.IsActive = true;
            }

            await _context.SaveChangesAsync();

            HttpContext.Session.SetString(
                "ML_USER_ID",
                existingToken.UserId ?? string.Empty);

            HttpContext.Session.SetString(
                "ML_NICKNAME",
                existingToken.Nickname ?? "N/A");

            HttpContext.Session.SetInt32(
                "ML_TOKEN_ID",
                existingToken.Id);

            TempData["Success"] =
                $"Cuenta de Mercado Libre conectada correctamente para el usuario {appUserName}.";

            return RedirectToAction(nameof(Status));
        }

        // =====================================================
        // DISCONNECT
        // =====================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Disconnect()
        {
            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
            var tokenId =
                HttpContext.Session.GetInt32(
                    "ML_TOKEN_ID");

            if (tokenId.HasValue)
            {
                var token =
                    await _context.MercadoLibreTokens
                        .Include(x => x.MarketplaceAccount)
                        .FirstOrDefaultAsync(x =>
                            x.Id == tokenId.Value &&
                            x.OrganizationId == organizationId);

                if (token != null)
                {
                    token.IsActive = false;

                    if (token.MarketplaceAccount != null)
                    {
                        token.MarketplaceAccount.Status = "Disconnected";
                        token.MarketplaceAccount.DisconnectedAt = DateTime.UtcNow;
                    }

                    token.UpdatedAt =
                        DateTime.UtcNow;

                    await _context.SaveChangesAsync();
                }
            }

            HttpContext.Session.Remove("ML_USER_ID");
            HttpContext.Session.Remove("ML_NICKNAME");
            HttpContext.Session.Remove("ML_TOKEN_ID");

            TempData["Success"] =
                "Mercado Libre disconnected successfully.";

            return RedirectToAction(nameof(Status));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DisconnectAccount(int accountId)
        {
            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
            var account = await _context.MarketplaceAccounts
                .Include(x => x.Tokens)
                .FirstOrDefaultAsync(x =>
                    x.Id == accountId &&
                    x.OrganizationId == organizationId &&
                    x.Marketplace == "MercadoLibre");

            if (account == null)
                return NotFound();

            foreach (var token in account.Tokens)
            {
                token.IsActive = false;
                token.UpdatedAt = DateTime.UtcNow;
            }

            account.Status = "Disconnected";
            account.DisconnectedAt = DateTime.UtcNow;

            var sessionTokenId = HttpContext.Session.GetInt32("ML_TOKEN_ID");
            if (sessionTokenId.HasValue && account.Tokens.Any(x => x.Id == sessionTokenId.Value))
            {
                HttpContext.Session.Remove("ML_USER_ID");
                HttpContext.Session.Remove("ML_NICKNAME");
                HttpContext.Session.Remove("ML_TOKEN_ID");
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = $"La cuenta {account.DisplayName ?? account.ExternalAccountId} se desconectó.";
            return RedirectToAction(nameof(Status));
        }

        // =====================================================
        // USER INFO
        // =====================================================

        [HttpGet]
        public async Task<IActionResult> Me()
        {
            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
            var result =
                await _mercadoLibreService
                    .GetMeAsync(organizationId);

            if (result == null)
                return BadRequest(
                    "Failed to retrieve user.");

            return Content(
                result,
                "application/json");
        }

        // =====================================================
        // CATEGORY PREDICTOR
        // =====================================================

        [HttpGet]
        public async Task<IActionResult> CategoryPredictor(
            string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return BadRequest(
                    "Title is required.");
            }

            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
            var result =
                await _mercadoLibreService
                    .PredictCategoryAsync(title, organizationId);

            if (result == null)
            {
                return BadRequest(
                    "Failed to predict category.");
            }

            return Content(
                result,
                "application/json");
        }

        // =====================================================
        // CATEGORY ATTRIBUTES
        // =====================================================

        [HttpGet]
        public async Task<IActionResult> CategoryAttributes(
            string categoryId)
        {
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                return BadRequest(
                    "CategoryId is required.");
            }

            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
            var result =
                await _mercadoLibreService
                    .GetCategoryAttributesAsync(
                        categoryId,
                        organizationId);

            if (result == null)
            {
                return BadRequest(
                    "Failed to retrieve attributes.");
            }

            return Content(
                result,
                "application/json");
        }

        // =====================================================
        // WEBHOOKS
        // =====================================================

        // Mercado Libre manda varios avisos seguidos por el mismo cambio; se agrupan por cuenta y tema.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> RecentNotifications = new();

        /// <summary>
        /// Avisos instantáneos de Mercado Libre (orders_v2, items, questions).
        /// Responde 200 de inmediato y deja el trabajo a Hangfire, como pide Mercado Libre (máx. 500 ms).
        /// </summary>
        [HttpPost]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Notifications(
            [FromBody] MercadoLibreNotification? notification,
            [FromServices] Hangfire.IBackgroundJobClient jobs,
            [FromServices] ILogger<MercadoLibreController> logger)
        {
            if (notification is null || string.IsNullOrWhiteSpace(notification.Topic) || notification.UserId is null)
                return Ok();

            var clientId = _configuration["MercadoLibre:ClientId"];
            if (!string.IsNullOrWhiteSpace(clientId) && notification.ApplicationId is not null &&
                !string.Equals(clientId, notification.ApplicationId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                logger.LogWarning("Ignoring Mercado Libre notification for application {ApplicationId}.", notification.ApplicationId);
                return Ok();
            }

            var userId = notification.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var account = await _context.MarketplaceAccounts
                .AsNoTracking()
                .Where(x => x.Marketplace == "MercadoLibre" && x.ExternalAccountId == userId && x.Status == "Connected")
                .Select(x => new { x.Id, x.OrganizationId })
                .FirstOrDefaultAsync();
            if (account is null)
                return Ok();

            var topic = notification.Topic.Trim().ToLowerInvariant();
            var resource = notification.Resource ?? string.Empty;

            switch (topic)
            {
                case "questions":
                {
                    var questionId = resource.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    if (questionId is not null && questionId.All(char.IsDigit) &&
                        ShouldEnqueue($"q:{account.Id}:{questionId}", TimeSpan.FromSeconds(30)))
                    {
                        jobs.Enqueue<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(
                            job => job.ProcessQuestionNotification(account.Id, questionId));
                    }
                    break;
                }
                case "orders_v2":
                case "orders":
                    if (ShouldEnqueue($"o:{account.OrganizationId}", TimeSpan.FromSeconds(45)))
                    {
                        jobs.Schedule<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(
                            job => job.SyncOrdersForOrganization(account.OrganizationId), TimeSpan.FromSeconds(15));
                    }
                    break;
                case "messages":
                case "claims":
                case "post_purchase":
                    if (ShouldEnqueue($"ps:{account.OrganizationId}", TimeSpan.FromSeconds(45)))
                    {
                        jobs.Schedule<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(
                            job => job.SyncPostSaleForOrganization(account.OrganizationId), TimeSpan.FromSeconds(10));
                    }
                    break;
                case "items":
                    if (ShouldEnqueue($"i:{account.OrganizationId}", TimeSpan.FromMinutes(3)))
                    {
                        jobs.Schedule<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(
                            job => job.SyncListingsForOrganization(account.OrganizationId), TimeSpan.FromSeconds(60));
                    }
                    break;
            }

            return Ok();
        }

        private static bool ShouldEnqueue(string key, TimeSpan window)
        {
            var now = DateTime.UtcNow;
            if (RecentNotifications.TryGetValue(key, out var last) && now - last < window)
                return false;
            RecentNotifications[key] = now;

            if (RecentNotifications.Count > 5000)
            {
                foreach (var entry in RecentNotifications.Where(x => now - x.Value > TimeSpan.FromMinutes(10)).ToList())
                    RecentNotifications.TryRemove(entry.Key, out _);
            }
            return true;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult NotificationsTest()
        {
            return Ok(
                "Notifications endpoint is available.");
        }
    }

    public class MercadoLibreNotification
    {
        [JsonPropertyName("resource")] public string? Resource { get; set; }
        [JsonPropertyName("user_id")] public long? UserId { get; set; }
        [JsonPropertyName("topic")] public string? Topic { get; set; }
        [JsonPropertyName("application_id")] public long? ApplicationId { get; set; }
        [JsonPropertyName("attempts")] public int? Attempts { get; set; }
    }

    // =====================================================
    // DTO TOKEN RESPONSE
    // =====================================================

    public class MercadoLibreTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
    }
}
