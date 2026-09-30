using MarketplaceSync.Web.Data;
using MarketplaceSync.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MarketplaceSync.Services.MercadoLibre
{
    public class MercadoLibreService : IMercadoLibreService
    {
        private readonly AppDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
private readonly IMercadoLibreAuthService _authService;
        public MercadoLibreService(
            AppDbContext context,
            IHttpClientFactory httpClientFactory,
            IMercadoLibreAuthService authService,
            IConfiguration configuration)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _authService = authService;
        }

        public async Task<bool> PublishProductAsync(int productId, Guid organizationId)
        {
            var product = await _context.Products
                .FirstOrDefaultAsync(x => x.Id == productId && x.OrganizationId == organizationId);

            if (product == null)
                return false;

            var credential = await _context.MercadoLibreTokens
                .Where(x => x.OrganizationId == organizationId && x.IsActive && x.MarketplaceAccountId != null)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefaultAsync();
            if (credential == null)
                return false;

            var publication = await _context.MarketplacePublications
                .FirstOrDefaultAsync(x => x.ProductId == productId &&
                    x.Marketplace == "MercadoLibre" &&
                    x.MarketplaceAccountId == credential.MarketplaceAccountId);
            if (publication == null)
                return false;

           var token =
    await _authService.GetValidAccessTokenAsync(organizationId, credential.MarketplaceAccountId!.Value);

            var client = _httpClientFactory.CreateClient();

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token);

            var payload = new
            {
                title = product.Title,
                price = publication.Price,
                currency_id = "MXN",
                available_quantity = publication.Stock,
                buying_mode = "buy_it_now",
                condition = "new",
                listing_type_id = "gold_special",
                category_id = publication.CategoryId
            };

            var response = await client.PostAsJsonAsync(
                "https://api.mercadolibre.com/items",
                payload);

            if (!response.IsSuccessStatusCode)
                return false;

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            publication.ExternalItemId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            publication.Permalink = root.TryGetProperty("permalink", out var permalink) ? permalink.GetString() : null;
            publication.Status = root.TryGetProperty("status", out var status) ? status.GetString() : "published";
            publication.PublishedAt = DateTime.UtcNow;
            publication.IsPublished = true;
            publication.UpdatedAt = DateTime.UtcNow;

            product.Status = "Published";

            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<string?> PredictCategoryAsync(string title, Guid organizationId)
        {
            var token = await GetAccessTokenAsync(organizationId);

            var client = _httpClientFactory.CreateClient();

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token);

            var response = await client.GetAsync(
                $"https://api.mercadolibre.com/sites/MLM/domain_discovery/search?limit=1&q={title}");

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync();
        }
        public async Task<string?> GetCategoryAttributesAsync(string categoryId, Guid organizationId)
{
    var token = await GetAccessTokenAsync(organizationId);

    var client = _httpClientFactory.CreateClient();

    var url =
        $"https://api.mercadolibre.com/categories/{Uri.EscapeDataString(categoryId)}/attributes";

    using var request = new HttpRequestMessage(HttpMethod.Get, url);

    request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", token);

    using var response = await client.SendAsync(request);

    if (!response.IsSuccessStatusCode)
        return null;

    return await response.Content.ReadAsStringAsync();
}
public async Task<string?> GetNicknameAsync(string accessToken)
{
    try
    {
        var client = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.mercadolibre.com/users/me"
        );

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(content);

        var root = document.RootElement;

        if (root.TryGetProperty("nickname", out var nicknameElement))
        {
            return nicknameElement.GetString();
        }

        return null;
    }
    catch
    {
        return null;
    }
}
public async Task<string?> GetMeAsync(Guid organizationId)
{
    var token = await GetAccessTokenAsync(organizationId);

    var client = _httpClientFactory.CreateClient();

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        "https://api.mercadolibre.com/users/me");

    request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", token);

    using var response = await client.SendAsync(request);

    if (!response.IsSuccessStatusCode)
        return null;

    return await response.Content.ReadAsStringAsync();
}
        public async Task<string> GetAccessTokenAsync(Guid organizationId)
        {
            var token = await _context.MercadoLibreTokens
                .Where(x => x.OrganizationId == organizationId && x.IsActive)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            if (token == null)
                throw new Exception("Mercado Libre token not found");

            return token.AccessToken;
        }
    }
}
