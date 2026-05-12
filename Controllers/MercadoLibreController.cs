using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
            var token = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync();

            return View(token);
        }
[HttpGet]
public async Task<IActionResult> CategoryPredictor(string title)
{
    if (string.IsNullOrWhiteSpace(title))
        return BadRequest("Title is required.");

    var token = await _context.MercadoLibreTokens
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefaultAsync();

    if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        return BadRequest("No Mercado Libre token found. Connect Mercado Libre first.");

    var client = _httpClientFactory.CreateClient();

    var url = $"https://api.mercadolibre.com/sites/MLM/domain_discovery/search?q={Uri.EscapeDataString(title)}&limit=5";

    var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

    var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();

    return Content(content, "application/json");
}
[HttpGet]
public async Task<IActionResult> CategoryAttributes(string categoryId)
{
    if (string.IsNullOrWhiteSpace(categoryId))
        return BadRequest("CategoryId is required.");

    var token = await _context.MercadoLibreTokens
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefaultAsync();

    if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        return BadRequest("No Mercado Libre token found. Connect Mercado Libre first.");

    var client = _httpClientFactory.CreateClient();

    var url = $"https://api.mercadolibre.com/categories/{Uri.EscapeDataString(categoryId)}/attributes";

    var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

    var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();

    return Content(content, "application/json");
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
                return BadRequest("Missing MercadoLibre AuthUrl.");

            var url =
                $"{authUrl}" +
                $"?response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";

            return Redirect(url);
        }

        [HttpGet]
        public async Task<IActionResult> Callback(string? code, string? error, string? error_description)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                return BadRequest($"Mercado Libre authorization error: {error} - {error_description}");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest("Mercado Libre did not return authorization code.");
            }

            var clientId = _configuration["MercadoLibre:ClientId"];
            var clientSecret = _configuration["MercadoLibre:ClientSecret"];
            var redirectUri = _configuration["MercadoLibre:RedirectUri"];
            var tokenUrl = _configuration["MercadoLibre:TokenUrl"];

            if (string.IsNullOrWhiteSpace(clientId))
                return BadRequest("Missing MercadoLibre ClientId.");

            if (string.IsNullOrWhiteSpace(clientSecret))
                return BadRequest("Missing MercadoLibre ClientSecret.");

            if (string.IsNullOrWhiteSpace(redirectUri))
                return BadRequest("Missing MercadoLibre RedirectUri.");

            if (string.IsNullOrWhiteSpace(tokenUrl))
                return BadRequest("Missing MercadoLibre TokenUrl.");

            var client = _httpClientFactory.CreateClient();

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri
            };

            var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, responseContent);
            }

            var tokenResponse = JsonSerializer.Deserialize<MercadoLibreTokenResponse>(
                responseContent,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return BadRequest("Could not parse Mercado Libre token response.");
            }

            var userId = tokenResponse.UserId.ToString();

            var existingToken = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(x => x.UserId == userId);

            if (existingToken == null)
            {
                existingToken = new MercadoLibreToken
                {
                    UserId = userId,
                    AccessToken = tokenResponse.AccessToken,
                    RefreshToken = tokenResponse.RefreshToken,
                    TokenType = tokenResponse.TokenType,
                    Scope = tokenResponse.Scope,
                    ExpiresIn = tokenResponse.ExpiresIn,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
                    CreatedAt = DateTime.UtcNow
                };

                _context.MercadoLibreTokens.Add(existingToken);
            }
            else
            {
                existingToken.AccessToken = tokenResponse.AccessToken;
                existingToken.RefreshToken = tokenResponse.RefreshToken;
                existingToken.TokenType = tokenResponse.TokenType;
                existingToken.Scope = tokenResponse.Scope;
                existingToken.ExpiresIn = tokenResponse.ExpiresIn;
                existingToken.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
                existingToken.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Status));
        }

        [HttpGet]
        public async Task<IActionResult> Me()
        {
            var token = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync();

            if (token == null)
            {
                return BadRequest("No Mercado Libre token found. Connect Mercado Libre first.");
            }

            var client = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.mercadolibre.com/users/me");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var response = await client.SendAsync(request);
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
}