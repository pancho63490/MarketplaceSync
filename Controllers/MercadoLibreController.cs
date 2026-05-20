using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace MarketplaceSync.Web.Controllers
{
    public class MercadoLibreController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly AppDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;

        public MercadoLibreController(
            IConfiguration configuration,
            AppDbContext context,
            IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _context = context;
            _httpClientFactory = httpClientFactory;
        }

        [HttpGet]
        public async Task<IActionResult> Status()
        {
            var tokens = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.UpdatedAt)
                .ToListAsync();

            return View(tokens);
        }

        [HttpGet]
        public IActionResult Connect()
        {
            var clientId = _configuration["MercadoLibre:ClientId"];
            var redirectUri = _configuration["MercadoLibre:RedirectUri"];
            var authUrl = _configuration["MercadoLibre:AuthUrl"];

            if (string.IsNullOrWhiteSpace(clientId))
                return BadRequest("Missing MercadoLibre ClientId.");

            if (string.IsNullOrWhiteSpace(redirectUri))
                return BadRequest("Missing MercadoLibre RedirectUri.");

            if (string.IsNullOrWhiteSpace(authUrl))
                authUrl = "https://auth.mercadolibre.com.mx/authorization";

            var state = Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString("ML_OAUTH_STATE", state);

            var url =
                $"{authUrl}" +
                $"?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&state={Uri.EscapeDataString(state)}";

            return Redirect(url);
        }

    [HttpGet]
public async Task<IActionResult> Callback(string? code, string? state, string? error, string? error_description)
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        TempData["Error"] = $"Mercado Libre regresó error: {error} {error_description}";
        return RedirectToAction(nameof(Status));
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        TempData["Error"] = "Mercado Libre no regresó código de autorización.";
        return RedirectToAction(nameof(Status));
    }

    var clientId = _configuration["MercadoLibre:ClientId"];
    var clientSecret = _configuration["MercadoLibre:ClientSecret"];
    var redirectUri = _configuration["MercadoLibre:RedirectUri"];
    var tokenUrl = _configuration["MercadoLibre:TokenUrl"];

    if (string.IsNullOrWhiteSpace(clientId))
    {
        TempData["Error"] = "Falta MercadoLibre:ClientId.";
        return RedirectToAction(nameof(Status));
    }

    if (string.IsNullOrWhiteSpace(clientSecret))
    {
        TempData["Error"] = "Falta MercadoLibre:ClientSecret.";
        return RedirectToAction(nameof(Status));
    }

    if (string.IsNullOrWhiteSpace(redirectUri))
    {
        TempData["Error"] = "Falta MercadoLibre:RedirectUri.";
        return RedirectToAction(nameof(Status));
    }

    if (string.IsNullOrWhiteSpace(tokenUrl))
    {
        tokenUrl = "https://api.mercadolibre.com/oauth/token";
    }

    var client = _httpClientFactory.CreateClient();

    using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);

    tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        { "grant_type", "authorization_code" },
        { "client_id", clientId },
        { "client_secret", clientSecret },
        { "code", code },
        { "redirect_uri", redirectUri }
    });

    using var tokenResponseHttp = await client.SendAsync(tokenRequest);
    var tokenContent = await tokenResponseHttp.Content.ReadAsStringAsync();

    if (!tokenResponseHttp.IsSuccessStatusCode)
    {
        TempData["Error"] = $"Error obteniendo token de Mercado Libre: {tokenContent}";
        return RedirectToAction(nameof(Status));
    }

    var tokenResponse = JsonSerializer.Deserialize<MercadoLibreTokenResponse>(
        tokenContent,
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

    if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
    {
        TempData["Error"] = $"Mercado Libre no regresó access_token: {tokenContent}";
        return RedirectToAction(nameof(Status));
    }

    string? nickname = null;

    try
    {
        using var meRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.mercadolibre.com/users/me");

        meRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenResponse.AccessToken);

        using var meResponse = await client.SendAsync(meRequest);
        var meContent = await meResponse.Content.ReadAsStringAsync();

        if (meResponse.IsSuccessStatusCode)
        {
            using var meDocument = JsonDocument.Parse(meContent);
            var root = meDocument.RootElement;

            if (root.TryGetProperty("nickname", out var nicknameElement))
            {
                nickname = nicknameElement.GetString();
            }
        }
    }
    catch
    {
        nickname = null;
    }

    var userId = tokenResponse.UserId.ToString();
    var expiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

    var existingToken = await _context.MercadoLibreTokens
        .FirstOrDefaultAsync(x => x.UserId == userId);

    var oldActiveTokens = await _context.MercadoLibreTokens
        .Where(x => x.IsActive)
        .ToListAsync();

    foreach (var oldToken in oldActiveTokens)
    {
        oldToken.IsActive = false;
    }

    if (existingToken == null)
    {
        existingToken = new MercadoLibreToken
        {
            UserId = userId,
            Nickname = nickname,
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            TokenType = tokenResponse.TokenType,
            Scope = tokenResponse.Scope,
            ExpiresIn = tokenResponse.ExpiresIn,
            ExpiresAt = expiresAt,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.MercadoLibreTokens.Add(existingToken);
    }
    else
    {
        existingToken.Nickname = nickname;
        existingToken.AccessToken = tokenResponse.AccessToken;
        existingToken.RefreshToken = tokenResponse.RefreshToken;
        existingToken.TokenType = tokenResponse.TokenType;
        existingToken.Scope = tokenResponse.Scope;
        existingToken.ExpiresIn = tokenResponse.ExpiresIn;
        existingToken.ExpiresAt = expiresAt;
        existingToken.IsActive = true;
        existingToken.UpdatedAt = DateTime.UtcNow;
    }

    await _context.SaveChangesAsync();

    HttpContext.Session.SetString("ML_USER_ID", userId);
    HttpContext.Session.SetString("ML_NICKNAME", nickname ?? userId);
    HttpContext.Session.SetInt32("ML_TOKEN_ID", existingToken.Id);

    TempData["Success"] = $"Cuenta de Mercado Libre conectada correctamente: {nickname ?? userId}";

    return RedirectToAction(nameof(Status));
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Disconnect()
{
    var tokenId = HttpContext.Session.GetInt32("ML_TOKEN_ID");

    if (tokenId.HasValue)
    {
        var token = await _context.MercadoLibreTokens
            .FirstOrDefaultAsync(x => x.Id == tokenId.Value);

        if (token != null)
        {
            token.IsActive = false;
            token.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
    }

    HttpContext.Session.Remove("ML_USER_ID");
    HttpContext.Session.Remove("ML_NICKNAME");
    HttpContext.Session.Remove("ML_TOKEN_ID");

    TempData["Success"] = "Cuenta de Mercado Libre desconectada de esta sesión.";

    return RedirectToAction(nameof(Status));
}

        [HttpGet]
        public async Task<IActionResult> Me()
        {
            var token = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return BadRequest("No Mercado Libre token found. Connect Mercado Libre first.");
            }

            var client = _httpClientFactory.CreateClient();

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.mercadolibre.com/users/me");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token.AccessToken);

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            return Content(content, "application/json");
        }

        [HttpGet]
        public async Task<IActionResult> CategoryPredictor(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest("Title is required.");

            var token = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                return BadRequest("No Mercado Libre token found. Connect Mercado Libre first.");

            var client = _httpClientFactory.CreateClient();

            var url =
                $"https://api.mercadolibre.com/sites/MLM/domain_discovery/search" +
                $"?q={Uri.EscapeDataString(title)}" +
                $"&limit=5";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token.AccessToken);

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            return Content(content, "application/json");
        }

        [HttpGet]
        public async Task<IActionResult> CategoryAttributes(string categoryId)
        {
            if (string.IsNullOrWhiteSpace(categoryId))
                return BadRequest("CategoryId is required.");

            var token = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();

            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                return BadRequest("No Mercado Libre token found. Connect Mercado Libre first.");

            var client = _httpClientFactory.CreateClient();

            var url =
                $"https://api.mercadolibre.com/categories/{Uri.EscapeDataString(categoryId)}/attributes";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token.AccessToken);

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            return Content(content, "application/json");
        }

        [HttpPost]
        public IActionResult Notifications()
        {
            return Ok();
        }

        [HttpGet]
        public IActionResult NotificationsTest()
        {
            return Ok("Notifications endpoint is available.");
        }
    }

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