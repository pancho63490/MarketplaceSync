using System.Net.Http.Headers;
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

            var tokens = await _context.MercadoLibreTokens
                .Where(x => x.OrganizationId == organizationId)
                .OrderByDescending(x => x.UpdatedAt)
                .ToListAsync();

            var activeToken = tokens
                .FirstOrDefault(x => x.IsActive);

            ViewBag.TotalAccounts = tokens.Count;

            ViewBag.ActiveAccounts =
                tokens.Count(x => x.IsActive);

            ViewBag.ActiveNickname =
                activeToken?.Nickname ?? "N/A";

            ViewBag.ActiveToken =
                activeToken != null
                    ? $"{activeToken.AccessToken[..Math.Min(10, activeToken.AccessToken.Length)]}..."
                    : "N/A";

            return View(tokens);
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

        [HttpPost]
        [AllowAnonymous]
        public IActionResult Notifications()
        {
            return Ok();
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult NotificationsTest()
        {
            return Ok(
                "Notifications endpoint is available.");
        }
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
