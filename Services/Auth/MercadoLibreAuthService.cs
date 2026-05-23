using System.Text.Json;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Auth
{
    public class MercadoLibreAuthService
        : IMercadoLibreAuthService
    {
        private readonly AppDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public MercadoLibreAuthService(
            AppDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public async Task<string> GetValidAccessTokenAsync()
        {
            var token = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();

            if (token == null)
                throw new Exception("No Mercado Libre token found.");

            var isExpired =
                token.ExpiresAt <= DateTime.UtcNow.AddMinutes(5);

            if (isExpired)
            {
                var refreshed =
                    await RefreshTokenAsync();

                if (!refreshed)
                    throw new Exception("Failed to refresh token.");

                token = await _context.MercadoLibreTokens
                    .OrderByDescending(x => x.UpdatedAt)
                    .FirstOrDefaultAsync();
            }

            return token!.AccessToken;
        }

        public async Task<bool> RefreshTokenAsync()
        {
            var token = await _context.MercadoLibreTokens
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();

            if (token == null)
                return false;

            var clientId =
                _configuration["MercadoLibre:ClientId"];

            var clientSecret =
                _configuration["MercadoLibre:ClientSecret"];

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
                new HttpRequestMessage(HttpMethod.Post, tokenUrl);

            request.Content =
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        { "grant_type", "refresh_token" },
                        { "client_id", clientId! },
                        { "client_secret", clientSecret! },
                        { "refresh_token", token.RefreshToken! }
                    });

            using var response =
                await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return false;

            var content =
                await response.Content.ReadAsStringAsync();

            using var document =
                JsonDocument.Parse(content);

            var root =
                document.RootElement;

            token.AccessToken =
                root.GetProperty("access_token").GetString();

            token.RefreshToken =
                root.GetProperty("refresh_token").GetString();

            token.ExpiresIn =
                root.GetProperty("expires_in").GetInt32();

            token.ExpiresAt =
                DateTime.UtcNow.AddSeconds(token.ExpiresIn);

            token.UpdatedAt =
                DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return true;
        }
    }
}