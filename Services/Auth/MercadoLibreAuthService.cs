using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Auth;

public class MercadoLibreAuthService : IMercadoLibreAuthService
{
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MercadoLibreAuthService> _logger;

    public MercadoLibreAuthService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<MercadoLibreAuthService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GetValidAccessTokenAsync(Guid organizationId, int marketplaceAccountId)
    {
        var token = await _context.MercadoLibreTokens
            .Where(x => x.OrganizationId == organizationId &&
                        x.MarketplaceAccountId == marketplaceAccountId &&
                        x.IsActive)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync();

        if (token == null)
            throw new InvalidOperationException("No hay una cuenta activa de Mercado Libre para la organización.");

        if (token.ExpiresAt <= DateTime.UtcNow.AddMinutes(5))
        {
            if (!await RefreshTokenAsync(token.Id))
                throw new InvalidOperationException("No fue posible renovar la conexión con Mercado Libre.");

            token = await _context.MercadoLibreTokens
                .AsNoTracking()
                .FirstAsync(x => x.Id == token.Id && x.OrganizationId == organizationId &&
                                 x.MarketplaceAccountId == marketplaceAccountId);
        }

        return token.AccessToken;
    }

    public async Task<bool> RefreshTokenAsync(int tokenId)
    {
        var token = await _context.MercadoLibreTokens
            .FirstOrDefaultAsync(x => x.Id == tokenId && x.IsActive);

        if (token == null || string.IsNullOrWhiteSpace(token.RefreshToken))
            return false;

        var clientId = _configuration["MercadoLibre:ClientId"];
        var clientSecret = _configuration["MercadoLibre:ClientSecret"];
        var tokenUrl = _configuration["MercadoLibre:TokenUrl"]
            ?? "https://api.mercadolibre.com/oauth/token";

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return false;

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = token.RefreshToken
            })
        };

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Falló la renovación del token de Mercado Libre {TokenId}. HTTP {StatusCode}.",
                token.Id,
                (int)response.StatusCode);
            return false;
        }

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        var accessToken = root.GetProperty("access_token").GetString();
        var refreshToken = root.GetProperty("refresh_token").GetString();

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            return false;

        token.AccessToken = accessToken;
        token.RefreshToken = refreshToken;
        token.ExpiresIn = root.GetProperty("expires_in").GetInt32();
        token.ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
        token.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<int> RefreshExpiringTokensAsync()
    {
        var tokenIds = await _context.MercadoLibreTokens
            .AsNoTracking()
            .Where(x => x.IsActive && x.ExpiresAt <= DateTime.UtcNow.AddMinutes(30))
            .Select(x => x.Id)
            .ToListAsync();

        var refreshed = 0;
        foreach (var tokenId in tokenIds)
        {
            try
            {
                if (await RefreshTokenAsync(tokenId))
                    refreshed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renovando el token de Mercado Libre {TokenId}.", tokenId);
            }
        }

        return refreshed;
    }
}
