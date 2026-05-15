using System.Text.Json;

namespace MarketplaceSync.Web.Services
{
    public class MercadoLibreCategoryService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public MercadoLibreCategoryService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<List<MercadoLibreCategorySuggestion>> SuggestCategoriesAsync(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new List<MercadoLibreCategorySuggestion>();

            var client = _httpClientFactory.CreateClient();

            var query = Uri.EscapeDataString(title.Trim());

            var url = $"https://api.mercadolibre.com/sites/MLM/domain_discovery/search?q={query}&limit=8";

            using var response = await client.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error consultando categorías Mercado Libre: {content}");

            var result = JsonSerializer.Deserialize<List<MercadoLibreCategorySuggestion>>(
                content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            return result ?? new List<MercadoLibreCategorySuggestion>();
        }
    }

    public class MercadoLibreCategorySuggestion
    {
        public string? Domain_Id { get; set; }
        public string? Domain_Name { get; set; }
        public string? Category_Id { get; set; }
        public string? Category_Name { get; set; }
        public List<MercadoLibreCategoryAttribute>? Attributes { get; set; }
    }

    public class MercadoLibreCategoryAttribute
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Value_Id { get; set; }
        public string? Value_Name { get; set; }
    }
}