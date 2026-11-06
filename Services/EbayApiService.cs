using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MarketplaceSync.Web.ViewModels;

namespace MarketplaceSync.Web.Services
{
    public class EbayApiService
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public EbayApiService(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            var configured = configuration["Ebay:ApiBaseUrl"];
            _apiBase = string.IsNullOrWhiteSpace(configured) ? "https://api.ebay.com" : configured.TrimEnd('/');
        }

        /// <summary>Base de la API de eBay; Ebay:ApiBaseUrl solo para pruebas.</summary>
        private readonly string _apiBase;

        /// <summary>true si hay credenciales de eBay (Ebay:ClientId y Ebay:ClientSecret).</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_configuration["Ebay:ClientId"]) && !string.IsNullOrWhiteSpace(_configuration["Ebay:ClientSecret"]);

        public async Task<ExtractedProductInfo> ExtractFromUrlOrSearchAsync(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("El link o texto de búsqueda está vacío.");

            var legacyItemId = ExtractLegacyItemId(input);

            if (!string.IsNullOrWhiteSpace(legacyItemId))
            {
                return await GetItemByLegacyIdAsync(legacyItemId, input);
            }

            return await SearchFirstItemAsync(input);
        }

        public async Task<ExtractedProductInfo> SearchFirstItemAsync(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                throw new ArgumentException("El texto de búsqueda está vacío.");

            var token = await GetApplicationTokenAsync();

            var client = _httpClientFactory.CreateClient();

            var query = Uri.EscapeDataString(searchText.Trim());

            var url =
                $"{_apiBase}/buy/browse/v1/item_summary/search" +
                $"?q={query}" +
                $"&limit=1" +
                $"&filter=buyingOptions:%7BFIXED_PRICE%7D";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error buscando producto en eBay: {content}");

            var result = JsonSerializer.Deserialize<EbaySearchResponse>(
                content,
                JsonOptions());

            var item = result?.ItemSummaries?.FirstOrDefault();

            if (item == null)
                throw new Exception("No se encontró ningún producto en eBay.");

            return MapSummaryToExtractedProductInfo(item, searchText);
        }

        public async Task<ExtractedProductInfo> GetItemByLegacyIdAsync(string legacyItemId, string originalUrl)
        {
            if (string.IsNullOrWhiteSpace(legacyItemId))
                throw new ArgumentException("El eBay item ID está vacío.");

            var token = await GetApplicationTokenAsync();

            var client = _httpClientFactory.CreateClient();

            var url =
                $"{_apiBase}/buy/browse/v1/item/get_item_by_legacy_id" +
                $"?legacy_item_id={Uri.EscapeDataString(legacyItemId)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error obteniendo producto eBay por ID {legacyItemId}: {content}");

            var item = JsonSerializer.Deserialize<EbayItemResponse>(
                content,
                JsonOptions());

            if (item == null)
                throw new Exception("No se pudo leer la respuesta del producto de eBay.");

            return MapItemToExtractedProductInfo(item, originalUrl);
        }

        /// <summary>
        /// Busca artículos nuevos de precio fijo ubicados en EE.UU. para el Radar de compra.
        /// Devuelve varias coincidencias para que el llamador elija la mejor.
        /// </summary>
        public async Task<IReadOnlyList<EbaySearchHit>> SearchItemsAsync(
            string searchText,
            int limit = 15,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return Array.Empty<EbaySearchHit>();

            var token = await GetApplicationTokenAsync();
            var client = _httpClientFactory.CreateClient();

            var query = Uri.EscapeDataString(searchText.Trim());
            var filter = Uri.EscapeDataString(
                "buyingOptions:{FIXED_PRICE},conditions:{NEW},itemLocationCountry:US,deliveryCountry:US");

            var url =
                $"{_apiBase}/buy/browse/v1/item_summary/search" +
                $"?q={query}" +
                $"&limit={Math.Clamp(limit, 1, 50)}" +
                $"&filter={filter}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");
            request.Headers.Add("X-EBAY-C-ENDUSERCTX", "contextualLocation=country%3DUS%2Czip%3D79901");

            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error buscando productos en eBay: {content}");

            var result = JsonSerializer.Deserialize<EbaySearchResponse>(content, JsonOptions());

            return (result?.ItemSummaries ?? new List<EbayItemSummary>())
                .Select(item =>
                {
                    var shipping = item.ShippingOptions?
                        .Select(x => ParseDecimal(x.ShippingCost?.Value))
                        .Where(x => x.HasValue)
                        .Select(x => x!.Value)
                        .DefaultIfEmpty()
                        .Min();

                    return new EbaySearchHit(
                        item.ItemId,
                        item.LegacyItemId,
                        item.Title ?? string.Empty,
                        ParseDecimal(item.Price?.Value),
                        item.ShippingOptions is { Count: > 0 } ? shipping : null,
                        item.Price?.Currency,
                        item.Image?.ImageUrl,
                        item.ItemWebUrl,
                        item.Condition,
                        item.Brand);
                })
                .Where(x => x.PriceUsd is > 0 &&
                            string.Equals(x.Currency ?? "USD", "USD", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private async Task<string> GetApplicationTokenAsync()
        {
            var clientId = _configuration["Ebay:ClientId"];
            var clientSecret = _configuration["Ebay:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Falta Ebay:ClientId.");

            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException("Falta Ebay:ClientSecret.");

            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")
            );

            var client = _httpClientFactory.CreateClient();

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_apiBase}/identity/v1/oauth2/token"
            );

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "scope", "https://api.ebay.com/oauth/api_scope" }
            });

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error generando token de eBay: {content}");

            var tokenResponse = JsonSerializer.Deserialize<EbayTokenResponse>(
                content,
                JsonOptions());

            if (string.IsNullOrWhiteSpace(tokenResponse?.AccessToken))
                throw new Exception($"eBay no regresó access_token válido: {content}");

            return tokenResponse.AccessToken;
        }

        private ExtractedProductInfo MapItemToExtractedProductInfo(EbayItemResponse item, string originalUrl)
        {
            var availability = item.EstimatedAvailabilities?.FirstOrDefault();

            var sourceStock = GetBestStock(
                availability?.EstimatedAvailableQuantity,
                availability?.EstimatedAvailabilityStatus);

            var availabilityText = GetAvailabilityText(
                availability?.EstimatedAvailabilityStatus,
                availability?.EstimatedAvailableQuantity,
                availability?.EstimatedSoldQuantity);

            var price = ParseDecimal(item.Price?.Value);

            return new ExtractedProductInfo
            {
                SourceUrl = !string.IsNullOrWhiteSpace(originalUrl)
                    ? originalUrl
                    : item.ItemWebUrl ?? string.Empty,

                SourceMarketplace = "eBay",
                SourceProductId = item.LegacyItemId ?? item.ItemId,
                Title = item.Title ?? "Producto eBay",
                Description = "Producto obtenido desde eBay Browse API.",
                SourcePrice = price,
                SourceCurrency = item.Price?.Currency,
                SourceStock = sourceStock,
                SourceAvailabilityText = availabilityText,
                ImageUrl = item.Image?.ImageUrl,
                Brand = GetAspectValue(item, "Brand", "Marca"),
                Model = GetAspectValue(item, "Model", "Modelo", "Manufacturer Part Number", "MPN"),
                SourceStatus = "ExtractedByEbayApi",
                LastSourceCheckAt = DateTime.UtcNow
            };
        }

        private ExtractedProductInfo MapSummaryToExtractedProductInfo(EbayItemSummary item, string originalInput)
        {
            var availability = item.EstimatedAvailabilities?.FirstOrDefault();

            var sourceStock = GetBestStock(
                availability?.EstimatedAvailableQuantity,
                availability?.EstimatedAvailabilityStatus);

            var availabilityText = GetAvailabilityText(
                availability?.EstimatedAvailabilityStatus,
                availability?.EstimatedAvailableQuantity,
                availability?.EstimatedSoldQuantity);

            var price = ParseDecimal(item.Price?.Value);

            return new ExtractedProductInfo
            {
                SourceUrl = item.ItemWebUrl ?? originalInput,
                SourceMarketplace = "eBay",
                SourceProductId = item.LegacyItemId ?? item.ItemId,
                Title = item.Title ?? "Producto eBay",
                Description = "Producto obtenido desde eBay Browse API.",
                SourcePrice = price,
                SourceCurrency = item.Price?.Currency,
                SourceStock = sourceStock,
                SourceAvailabilityText = availabilityText,
                ImageUrl = item.Image?.ImageUrl,
                Brand = item.Brand,
                Model = null,
                SourceStatus = "ExtractedByEbayApi",
                LastSourceCheckAt = DateTime.UtcNow
            };
        }

        private int? GetBestStock(int? quantity, string? status)
        {
            if (quantity.HasValue)
                return quantity.Value;

            if (string.Equals(status, "IN_STOCK", StringComparison.OrdinalIgnoreCase))
                return 1;

            if (string.Equals(status, "LIMITED_STOCK", StringComparison.OrdinalIgnoreCase))
                return 1;

            if (string.Equals(status, "OUT_OF_STOCK", StringComparison.OrdinalIgnoreCase))
                return 0;

            return null;
        }

        private string GetAvailabilityText(string? status, int? quantity, int? soldQuantity)
        {
            var soldText = soldQuantity.HasValue
                ? $" / {soldQuantity.Value} sold"
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(status))
            {
                return status.ToUpperInvariant() switch
                {
                    "IN_STOCK" => quantity.HasValue
                        ? $"In Stock - {quantity.Value} available{soldText}"
                        : $"In Stock{soldText}",

                    "OUT_OF_STOCK" => $"Out of Stock{soldText}",

                    "LIMITED_STOCK" => quantity.HasValue
                        ? $"Limited Stock - {quantity.Value} available{soldText}"
                        : $"Limited Stock{soldText}",

                    _ => $"{status}{soldText}"
                };
            }

            if (quantity.HasValue && quantity.Value > 0)
                return $"In Stock - {quantity.Value} available{soldText}";

            if (quantity.HasValue && quantity.Value == 0)
                return $"Out of Stock{soldText}";

            return !string.IsNullOrWhiteSpace(soldText)
                ? $"Availability not provided by eBay{soldText}"
                : "Availability not provided by eBay";
        }

        private string? ExtractLegacyItemId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var decoded = Uri.UnescapeDataString(input);

            var match = Regex.Match(
                decoded,
                @"(?:/itm/[^/?#]*?/|/itm/|item=|itm/)(\d{9,15})",
                RegexOptions.IgnoreCase);

            if (match.Success)
                return match.Groups[1].Value;

            var simpleNumber = Regex.Match(decoded, @"\b\d{9,15}\b");

            if (simpleNumber.Success)
                return simpleNumber.Value;

            return null;
        }

        private decimal? ParseDecimal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (decimal.TryParse(
                    value,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var result))
            {
                return result;
            }

            return null;
        }

        private string? GetAspectValue(EbayItemResponse item, params string[] names)
        {
            if (item.LocalizedAspects == null || item.LocalizedAspects.Count == 0)
                return null;

            return item.LocalizedAspects
                .FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.Name) &&
                    names.Any(name => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                ?.Value;
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }
    }

    public class EbayTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }
    }

    public class EbaySearchResponse
    {
        [JsonPropertyName("itemSummaries")]
        public List<EbayItemSummary>? ItemSummaries { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("href")]
        public string? Href { get; set; }
    }

    public class EbayItemSummary
    {
        [JsonPropertyName("itemId")]
        public string? ItemId { get; set; }

        [JsonPropertyName("legacyItemId")]
        public string? LegacyItemId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("price")]
        public EbayPrice? Price { get; set; }

        [JsonPropertyName("image")]
        public EbayImage? Image { get; set; }

        [JsonPropertyName("itemWebUrl")]
        public string? ItemWebUrl { get; set; }

        [JsonPropertyName("condition")]
        public string? Condition { get; set; }

        [JsonPropertyName("brand")]
        public string? Brand { get; set; }

        [JsonPropertyName("estimatedAvailabilities")]
        public List<EbayAvailability>? EstimatedAvailabilities { get; set; }

        [JsonPropertyName("shippingOptions")]
        public List<EbayShippingOption>? ShippingOptions { get; set; }
    }

    public class EbayShippingOption
    {
        [JsonPropertyName("shippingCostType")]
        public string? ShippingCostType { get; set; }

        [JsonPropertyName("shippingCost")]
        public EbayPrice? ShippingCost { get; set; }
    }

    public sealed record EbaySearchHit(
        string? ItemId,
        string? LegacyItemId,
        string Title,
        decimal? PriceUsd,
        decimal? ShippingUsd,
        string? Currency,
        string? ImageUrl,
        string? ItemWebUrl,
        string? Condition,
        string? Brand);

    public class EbayItemResponse
    {
        [JsonPropertyName("itemId")]
        public string? ItemId { get; set; }

        [JsonPropertyName("legacyItemId")]
        public string? LegacyItemId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("price")]
        public EbayPrice? Price { get; set; }

        [JsonPropertyName("image")]
        public EbayImage? Image { get; set; }

        [JsonPropertyName("itemWebUrl")]
        public string? ItemWebUrl { get; set; }

        [JsonPropertyName("condition")]
        public string? Condition { get; set; }

        [JsonPropertyName("estimatedAvailabilities")]
        public List<EbayAvailability>? EstimatedAvailabilities { get; set; }

        [JsonPropertyName("localizedAspects")]
        public List<EbayLocalizedAspect>? LocalizedAspects { get; set; }
    }

    public class EbayPrice
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }
    }

    public class EbayImage
    {
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }

    public class EbayAvailability
    {
        [JsonPropertyName("estimatedAvailabilityStatus")]
        public string? EstimatedAvailabilityStatus { get; set; }

        [JsonPropertyName("estimatedAvailableQuantity")]
        public int? EstimatedAvailableQuantity { get; set; }

        [JsonPropertyName("estimatedSoldQuantity")]
        public int? EstimatedSoldQuantity { get; set; }
    }

    public class EbayLocalizedAspect
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}