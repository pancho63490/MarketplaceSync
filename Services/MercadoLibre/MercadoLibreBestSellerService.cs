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
    private readonly string _apiBase;

    public MercadoLibreBestSellerService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<MercadoLibreBestSellerService> logger)
    {
        var configured = configuration["MercadoLibre:ApiBaseUrl"];
        _apiBase = string.IsNullOrWhiteSpace(configured) ? "https://api.mercadolibre.com" : configured.TrimEnd('/');
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

        // La categoría trae su ruta (path_from_root). Mercado Libre no publica ranking para todas
        // las subcategorías: si la elegida no tiene, se prueba con la categoría superior.
        using var category = await GetJsonAsync(
            client,
            $"{_apiBase}/categories/{Uri.EscapeDataString(categoryId)}",
            accessToken,
            cancellationToken);

        var candidates = new List<(string Id, string Name)>
        {
            (categoryId, ReadString(category.RootElement, "name") ?? categoryId)
        };
        if (category.RootElement.TryGetProperty("path_from_root", out var path) && path.ValueKind == JsonValueKind.Array)
        {
            var parents = path.EnumerateArray()
                .Select(x => (Id: ReadString(x, "id") ?? string.Empty, Name: ReadString(x, "name") ?? string.Empty))
                .Where(x => x.Id.Length > 0 && !x.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .Take(3);
            candidates.AddRange(parents);
        }

        JsonDocument? highlights = null;
        (string Id, string Name) used = candidates[0];
        HttpRequestException? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var document = await GetJsonAsync(
                    client,
                    $"{_apiBase}/highlights/MLM/category/{Uri.EscapeDataString(candidate.Id)}",
                    accessToken,
                    cancellationToken);
                if (document.RootElement.TryGetProperty("content", out var probe) &&
                    probe.ValueKind == JsonValueKind.Array && probe.GetArrayLength() > 0)
                {
                    highlights = document;
                    used = candidate;
                    break;
                }

                document.Dispose();
            }
            catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
            {
                // Sin ranking en esta categoría: probar la superior.
                lastError = exception;
            }
        }

        if (highlights is null)
        {
            throw lastError ?? new HttpRequestException(
                "Mercado Libre no tiene ranking de más vendidos para esta categoría ni para sus categorías superiores.",
                null,
                System.Net.HttpStatusCode.NotFound);
        }

        if (!used.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Category {CategoryId} has no best-seller ranking; using parent {ParentId}.",
                categoryId, used.Id);
        }

        using (highlights)
        {
            var content = highlights.RootElement.GetProperty("content");
            var rankedEntries = content.EnumerateArray()
                .Take(limit)
                .Select((entry, index) => new
                {
                    Id = ReadString(entry, "id") ?? string.Empty,
                    Type = ReadString(entry, "type") ?? "UNKNOWN",
                    Position = ReadInt32(entry, "position") ?? index + 1
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .DistinctBy(x => x.Id)
                .ToArray();
            var resolved = await Task.WhenAll(rankedEntries.Select(entry =>
                ResolveEntryAsync(client, entry.Id, entry.Type, entry.Position, accessToken, cancellationToken)));

            var result = new MercadoLibreBestSellerResult(
                used.Id,
                used.Name,
                used.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase)
                    ? ReadInt32(category.RootElement, "total_items_in_this_category")
                    : null,
                DateTime.UtcNow,
                resolved.OrderBy(x => x.Position).ToArray());

            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
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
                $"{_apiBase}/{resource}",
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
                ReadString(root, "permalink"),
                true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (type.Equals("USER_PRODUCT", StringComparison.OrdinalIgnoreCase))
            {
                var catalogMatch = await TryResolveUserProductFromCatalogAsync(
                    client,
                    id,
                    position,
                    accessToken,
                    cancellationToken);
                if (catalogMatch is not null)
                    return catalogMatch;
            }

            _logger.LogInformation(
                exception,
                "Mercado Libre did not expose details for best seller {Type} {Id}.",
                type,
                id);
            return Fallback(position, id, type);
        }
    }

    private static MercadoLibreBestSellerItem Fallback(int position, string id, string type) =>
        new(position, id, type, $"Ficha no disponible ({id})", null, null, null, null, null, null, false);

    private async Task<MercadoLibreBestSellerItem?> TryResolveUserProductFromCatalogAsync(
        HttpClient client,
        string userProductId,
        int position,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_apiBase}/products/search" +
                      $"?status=active&site_id=MLM&limit=1&q={Uri.EscapeDataString(userProductId)}";
            using var document = await GetJsonAsync(client, url, accessToken, cancellationToken);
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
                return null;

            var root = results[0];
            var catalogProductId = ReadString(root, "id");
            var name = ReadString(root, "name") ?? ReadString(root, "title");
            if (string.IsNullOrWhiteSpace(catalogProductId) || string.IsNullOrWhiteSpace(name))
                return null;

            var buyBox = root.TryGetProperty("buy_box_winner", out var buyBoxElement) &&
                         buyBoxElement.ValueKind == JsonValueKind.Object
                ? buyBoxElement
                : default;
            var imageUrl = ReadString(root, "secure_thumbnail") ??
                           ReadString(root, "thumbnail") ??
                           ReadFirstPicture(root) ??
                           (buyBox.ValueKind == JsonValueKind.Object
                               ? (ReadString(buyBox, "secure_thumbnail") ??
                                  ReadString(buyBox, "thumbnail") ??
                                  ReadFirstPicture(buyBox))
                               : null);
            var price = ReadDecimal(root, "price") ??
                        (buyBox.ValueKind == JsonValueKind.Object ? ReadDecimal(buyBox, "price") : null);
            var currencyId = ReadString(root, "currency_id") ??
                             (buyBox.ValueKind == JsonValueKind.Object ? ReadString(buyBox, "currency_id") : null);
            var permalink = ReadString(root, "permalink") ??
                            $"https://www.mercadolibre.com.mx/p/{Uri.EscapeDataString(catalogProductId)}";

            return new MercadoLibreBestSellerItem(
                position,
                userProductId,
                "USER_PRODUCT",
                name,
                ReadAttributeValue(root, "BRAND"),
                ReadAttributeValue(root, "MODEL"),
                imageUrl,
                price,
                currencyId,
                permalink,
                true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                exception,
                "Could not find a catalog match for user product {UserProductId}.",
                userProductId);
            return null;
        }
    }

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
                $"Mercado Libre respondió HTTP {(int)response.StatusCode} al consultar el ranking.",
                null,
                response.StatusCode);
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
