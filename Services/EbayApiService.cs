using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceSync.Web.ViewModels;

namespace MarketplaceSync.Web.Services
{
    public class EbayApiService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public EbayApiService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public async Task<string> GetApplicationTokenAsync()
        {
            var clientId = _configuration["Ebay:ClientId"];
            var clientSecret = _configuration["Ebay:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Falta Ebay:ClientId.");

            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException("Falta Ebay:ClientSecret.");

            var client = _httpClientFactory.CreateClient();

            var basicToken = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")
            );

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.ebay.com/identity/v1/oauth2/token"
            );

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);

            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "https://api.ebay.com/oauth/api_scope"
            });

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error obteniendo token de eBay: {content}");

            using var doc = JsonDocument.Parse(content);

            return doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new Exception("eBay no regresó access_token.");
        }

        public async Task<ExtractedProductInfo> ExtractFromUrlOrSearchAsync(string input)
        {
            var legacyItemId = ExtractLegacyItemId(input);

            if (!string.IsNullOrWhiteSpace(legacyItemId))
            {
                return await GetItemByLegacyIdAsync(legacyItemId, input);
            }

            var keyword = CleanKeyword(input);

            return await SearchFirstItemAsync(keyword);
        }

        public async Task<ExtractedProductInfo> GetItemByLegacyIdAsync(string legacyItemId, string originalUrl)
        {
            var token = await GetApplicationTokenAsync();
            var marketplaceId = _configuration["Ebay:MarketplaceId"] ?? "EBAY_US";

            var url =
                $"https://api.ebay.com/buy/browse/v1/item/get_item_by_legacy_id?legacy_item_id={Uri.EscapeDataString(legacyItemId)}";

            var client = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-EBAY-C-MARKETPLACE-ID", marketplaceId);

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new ExtractedProductInfo
                {
                    SourceMarketplace = "eBay",
                    SourceUrl = originalUrl,
                    SourceProductId = legacyItemId,
                    Title = "Producto eBay detectado, pero no se pudo obtener detalle",
                    SourceStock = 1,
                    SourceStatus = "EbayApiDetailError",
                    Description = content
                };
            }

            using var doc = JsonDocument.Parse(content);
            var item = doc.RootElement;

            return MapItemToExtractedProduct(item, originalUrl, legacyItemId, "ExtractedByEbayApi");
        }

        public async Task<ExtractedProductInfo> SearchFirstItemAsync(string searchText)
        {
            var token = await GetApplicationTokenAsync();

            var marketplaceId = _configuration["Ebay:MarketplaceId"] ?? "EBAY_US";
            var query = Uri.EscapeDataString(searchText);

            var url =
                $"https://api.ebay.com/buy/browse/v1/item_summary/search?q={query}&limit=1&filter=buyingOptions:%7BFIXED_PRICE%7D";

            var client = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-EBAY-C-MARKETPLACE-ID", marketplaceId);

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new ExtractedProductInfo
                {
                    SourceMarketplace = "eBay",
                    SourceUrl = searchText,
                    Title = "Producto eBay no encontrado",
                    SourceStock = 1,
                    SourceStatus = "EbayApiSearchError",
                    Description = content
                };
            }

            using var doc = JsonDocument.Parse(content);

            if (!doc.RootElement.TryGetProperty("itemSummaries", out var items) ||
                items.GetArrayLength() == 0)
            {
                return new ExtractedProductInfo
                {
                    SourceMarketplace = "eBay",
                    SourceUrl = searchText,
                    Title = "Producto eBay no encontrado",
                    SourceStock = 1,
                    SourceStatus = "NotFound",
                    Description = "eBay Browse API no regresó resultados."
                };
            }

            var item = items[0];

            return MapItemToExtractedProduct(item, searchText, null, "ExtractedByEbayApi");
        }

        private ExtractedProductInfo MapItemToExtractedProduct(
            JsonElement item,
            string fallbackUrl,
            string? fallbackProductId,
            string sourceStatus)
        {
            string? title = item.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString()
                : null;

            string? itemWebUrl = item.TryGetProperty("itemWebUrl", out var urlProp)
                ? urlProp.GetString()
                : fallbackUrl;

            string? itemId = item.TryGetProperty("itemId", out var idProp)
                ? idProp.GetString()
                : fallbackProductId;

            string? imageUrl = null;

            if (item.TryGetProperty("image", out var imageProp) &&
                imageProp.TryGetProperty("imageUrl", out var imageUrlProp))
            {
                imageUrl = imageUrlProp.GetString();
            }

            decimal? price = null;
            string? currency = null;

            if (item.TryGetProperty("price", out var priceProp))
            {
                if (priceProp.TryGetProperty("value", out var valueProp) &&
                    decimal.TryParse(valueProp.GetString(), out var parsedPrice))
                {
                    price = parsedPrice;
                }

                if (priceProp.TryGetProperty("currency", out var currencyProp))
                {
                    currency = currencyProp.GetString();
                }
            }

            string? condition = item.TryGetProperty("condition", out var conditionProp)
                ? conditionProp.GetString()
                : null;

            return new ExtractedProductInfo
            {
                SourceMarketplace = "eBay",
                SourceUrl = itemWebUrl ?? fallbackUrl,
                SourceProductId = itemId,
                Title = title ?? "Producto eBay",
                ImageUrl = imageUrl,
                SourcePrice = price,
                SourceCurrency = currency ?? "USD",
                SourceStock = 1,
                SourceStatus = sourceStatus,
                Condition = condition,
                Description = "Producto obtenido desde eBay Browse API."
            };
        }

        private string? ExtractLegacyItemId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var patterns = new[]
            {
                @"/itm/(?:.*?/)?(\d{9,15})",
                @"[?&]itm=(\d{9,15})",
                @"[?&]item=(\d{9,15})",
                @"\b(\d{9,15})\b"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);

                if (match.Success)
                    return match.Groups[1].Value;
            }

            return null;
        }

        private string CleanKeyword(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "phone case";

            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return input.Trim();

            var text = uri.AbsolutePath;

            text = Regex.Replace(text, @"/itm/|/sch/|/p/", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[-_/]+", " ");
            text = Regex.Replace(text, @"\d{9,15}", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "phone case";

            return text;
        }
    }
}