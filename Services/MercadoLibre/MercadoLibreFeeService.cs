using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed class MercadoLibreFeeService : IMercadoLibreFeeService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MercadoLibreFeeService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<MercadoLibreFeeEstimate> EstimateAsync(
        string accessToken,
        string categoryId,
        decimal price,
        string currencyId,
        string listingTypeId,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["price"] = price.ToString(CultureInfo.InvariantCulture),
            ["category_id"] = categoryId,
            ["currency_id"] = currencyId,
            ["listing_type_id"] = listingTypeId
        };
        var url = QueryString.Create(query).ToUriComponent();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.mercadolibre.com/sites/MLM/listing_prices{url}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Mercado Libre respondió HTTP {(int)response.StatusCode} al calcular los costos por vender.");
        }

        using var document = JsonDocument.Parse(content);
        var entry = FindListingPrice(document.RootElement, listingTypeId);
        if (!entry.HasValue)
            throw new InvalidOperationException("Mercado Libre no devolvió costos para esta publicación.");

        var saleFee = ReadDecimal(entry.Value, "sale_fee_amount") ?? 0m;
        var percentageFee = 0m;
        var fixedFee = 0m;
        if (entry.Value.TryGetProperty("sale_fee_details", out var details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            percentageFee = ReadDecimal(details, "percentage_fee") ?? 0m;
            fixedFee = ReadDecimal(details, "fixed_fee") ?? 0m;
        }

        return new MercadoLibreFeeEstimate(
            saleFee,
            percentageFee,
            fixedFee,
            ReadString(entry.Value, "currency_id") ?? currencyId,
            ReadString(entry.Value, "listing_type_id") ?? listingTypeId);
    }

    private static JsonElement? FindListingPrice(JsonElement element, string listingTypeId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (string.Equals(
                    ReadString(element, "listing_type_id"),
                    listingTypeId,
                    StringComparison.OrdinalIgnoreCase))
                return element;

            foreach (var property in element.EnumerateObject())
            {
                var match = FindListingPrice(property.Value, listingTypeId);
                if (match.HasValue)
                    return match;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var match = FindListingPrice(child, listingTypeId);
                if (match.HasValue)
                    return match;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? ReadDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDecimal(out var parsed)
            ? parsed
            : null;
}
