using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed class MercadoLibreBestSellerService : IMercadoLibreBestSellerService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MercadoLibreBestSellerService> _logger;

    public MercadoLibreBestSellerService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<MercadoLibreBestSellerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<MercadoLibreBestSellerResult> GetTopAsync(
        string categoryId,
        string accessToken,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        categoryId = categoryId.Trim().ToUpperInvariant();
        limit = Math.Clamp(limit, 1, 20);
        var cacheKey = $"meli-best-sellers:MLM:{categoryId}:{limit}";
        if (_cache.TryGetValue(cacheKey, out MercadoLibreBestSellerResult? cached) && cached is not null)
            return cached;

        var client = _httpClientFactory.CreateClient();
        var categoryTask = GetJsonAsync(
            client,
            $"https://api.mercadolibre.com/categories/{Uri.EscapeDataString(categoryId)}",
            accessToken,
            cancellationToken);
        using var highlights = await GetJsonAsync(
            client,
            $"https://api.mercadolibre.com/highlights/MLM/category/{Uri.EscapeDataString(categoryId)}",
            accessToken,
            cancellationToken);

        if (!highlights.RootElement.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Mercado Libre no devolvió un ranking para esta categoría.");
        }

        var rankedEntries = content.EnumerateArray()
            .Take(limit)
            .Select((entry, index) => new
            {
                Id = ReadString(entry, "id") ?? string.Empty,
                Type = ReadString(entry, "type") ?? "UNKNOWN",
                Position = ReadInt32(entry, "position") ?? index + 1
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToArray();
        var resolved = await Task.WhenAll(rankedEntries.Select(entry =>
            ResolveEntryAsync(client, entry.Id, entry.Type, entry.Position, accessToken, cancellationToken)));
        using var category = await categoryTask;
        var categoryName = ReadString(category.RootElement, "name") ?? categoryId;
        var result = new MercadoLibreBestSellerResult(
            categoryId,
            categoryName,
            ReadInt32(category.RootElement, "total_items_in_this_category"),
            DateTime.UtcNow,
            resolved.OrderBy(x => x.Position).ToArray());

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private async Task<MercadoLibreBestSellerItem> ResolveEntryAsync(
        HttpClient client,
        string id,
        string type,
        int position,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var resource = type.ToUpperInvariant() switch
        {
            "ITEM" => $"items/{Uri.EscapeDataString(id)}",
            "PRODUCT" => $"products/{Uri.EscapeDataString(id)}",
            "USER_PRODUCT" => $"user-products/{Uri.EscapeDataString(id)}",
            _ => null
        };

        if (resource is null)
            return Fallback(position, id, type);

        try
        {
            using var document = await GetJsonAsync(
                client,
                $"https://api.mercadolibre.com/{resource}",
                accessToken,
                cancellationToken);
            var root = document.RootElement;
            var buyBox = root.TryGetProperty("buy_box_winner", out var buyBoxElement) &&
                         buyBoxElement.ValueKind == JsonValueKind.Object
                ? buyBoxElement
                : default;
            var name = ReadString(root, "title") ??
                       ReadString(root, "name") ??
                       ReadString(root, "family_name") ??
                       id;
            var imageUrl = ReadString(root, "secure_thumbnail") ??
                           ReadString(root, "thumbnail") ??
                           ReadFirstPicture(root);
            var brand = ReadAttributeValue(root, "BRAND");
            var model = ReadAttributeValue(root, "MODEL");
            var price = ReadDecimal(root, "price") ??
                        (buyBox.ValueKind == JsonValueKind.Object ? ReadDecimal(buyBox, "price") : null);
            var currencyId = ReadString(root, "currency_id") ??
                             (buyBox.ValueKind == JsonValueKind.Object ? ReadString(buyBox, "currency_id") : null);

            return new MercadoLibreBestSellerItem(
                position,
                id,
                type,
                name,
                brand,
                model,
                imageUrl,
                price,
                currencyId,
                ReadString(root, "permalink"));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "Could not resolve Mercado Libre best seller {Type} {Id}.", type, id);
            return Fallback(position, id, type);
        }
    }

    private static MercadoLibreBestSellerItem Fallback(int position, string id, string type) =>
        new(position, id, type, $"Producto {id}", null, null, null, null, null, null);

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Mercado Libre respondió HTTP {(int)response.StatusCode} al consultar el ranking.");
        }

        return JsonDocument.Parse(content);
    }

    private static string? ReadFirstPicture(JsonElement root)
    {
        if (!root.TryGetProperty("pictures", out var pictures) ||
            pictures.ValueKind != JsonValueKind.Array ||
            pictures.GetArrayLength() == 0)
            return null;

        var picture = pictures[0];
        return ReadString(picture, "secure_url") ?? ReadString(picture, "url");
    }

    private static string? ReadAttributeValue(JsonElement root, string attributeId)
    {
        if (!root.TryGetProperty("attributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (!string.Equals(ReadString(attribute, "id"), attributeId, StringComparison.OrdinalIgnoreCase))
                continue;

            return ReadString(attribute, "value_name");
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static decimal? ReadDecimal(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetDecimal(out var parsed)
            ? parsed
            : null;
}
