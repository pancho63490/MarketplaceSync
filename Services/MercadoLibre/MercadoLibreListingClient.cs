using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceSync.Web.ViewModels;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Valor elegido para un atributo de Mercado Libre (se guarda en la publicación).</summary>
public sealed record SavedAttributeValue(string Id, string? ValueId, string? ValueName);

public sealed record ListingPublishResult(
    bool Success,
    string? ItemId,
    string? Permalink,
    string? Status,
    string? ErrorMessage);

/// <summary>
/// Llamadas a Mercado Libre para publicar: atributos obligatorios de una categoría,
/// crear o actualizar un artículo simple y guardar su descripción.
/// </summary>
public sealed class MercadoLibreListingClient
{
    private const string ApiBase = "https://api.mercadolibre.com";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MercadoLibreListingClient> _logger;

    public MercadoLibreListingClient(IHttpClientFactory httpClientFactory, ILogger<MercadoLibreListingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Atributos obligatorios de la categoría con sus opciones (el recurso es público; el token es opcional).</summary>
    public async Task<List<MercadoLibreAttributeInput>> GetRequiredAttributesAsync(
        string categoryId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MercadoLibreAttributeInput>();
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{ApiBase}/categories/{Uri.EscapeDataString(categoryId)}/attributes");
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Mercado Libre rechazó la categoría {categoryId} con HTTP {(int)response.StatusCode}.");

        var json = JsonNode.Parse(content)?.AsArray();
        if (json == null)
            return result;

        foreach (var node in json)
        {
            if (node == null || node["tags"]?["required"]?.GetValue<bool>() != true)
                continue;

            var id = node["id"]?.GetValue<string>() ?? string.Empty;
            var input = new MercadoLibreAttributeInput
            {
                Id = id,
                Name = node["name"]?.GetValue<string>() ?? id,
                Required = true,
                ValueType = node["value_type"]?.GetValue<string>()
            };

            foreach (var valueNode in node["values"]?.AsArray() ?? new JsonArray())
            {
                var valueName = valueNode?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(valueName))
                {
                    input.Values.Add(new MercadoLibreAttributeValueOption
                    {
                        Id = valueNode?["id"]?.GetValue<string>(),
                        Name = valueName
                    });
                }
            }

            result.Add(input);
        }

        return result;
    }

    /// <summary>
    /// Crea (POST) o actualiza (PUT) un artículo sin variaciones y después guarda la descripción.
    /// </summary>
    public async Task<ListingPublishResult> PublishSimpleItemAsync(
        string accessToken,
        string? existingItemId,
        string title,
        string categoryId,
        decimal price,
        int quantity,
        string condition,
        string listingTypeId,
        string? imageUrl,
        IReadOnlyList<SavedAttributeValue> attributes,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var updating = !string.IsNullOrWhiteSpace(existingItemId);
        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["price"] = Math.Round(price, 2),
            ["available_quantity"] = quantity,
            ["attributes"] = attributes
                .Where(a => !string.IsNullOrWhiteSpace(a.ValueId) || !string.IsNullOrWhiteSpace(a.ValueName))
                .Select(a => !string.IsNullOrWhiteSpace(a.ValueId)
                    ? (object)new { id = a.Id, value_id = a.ValueId }
                    : new { id = a.Id, value_name = a.ValueName!.Trim() })
                .ToArray()
        };

        if (!string.IsNullOrWhiteSpace(imageUrl))
            payload["pictures"] = new object[] { new { source = imageUrl } };

        if (!updating)
        {
            // Estos campos solo se aceptan al crear el artículo.
            payload["category_id"] = categoryId;
            payload["currency_id"] = "MXN";
            payload["buying_mode"] = "buy_it_now";
            payload["condition"] = condition;
            payload["listing_type_id"] = listingTypeId;
        }

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            updating ? HttpMethod.Put : HttpMethod.Post,
            updating ? $"{ApiBase}/items/{Uri.EscapeDataString(existingItemId!)}" : $"{ApiBase}/items");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new ListingPublishResult(false, existingItemId, null, null, DescribeError(content));

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var itemId = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : existingItemId;
        var permalink = root.TryGetProperty("permalink", out var linkElement) ? linkElement.GetString() : null;
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : "active";

        if (!string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(description))
            await UpsertDescriptionAsync(accessToken, itemId!, description!, updating, cancellationToken);

        return new ListingPublishResult(true, itemId, permalink, status, null);
    }

    /// <summary>La descripción va en un recurso aparte: POST al crear, PUT para reemplazarla.</summary>
    public async Task<bool> UpsertDescriptionAsync(
        string accessToken,
        string itemId,
        string description,
        bool replace,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var body = JsonSerializer.Serialize(new { plain_text = description.Trim() });

            async Task<HttpResponseMessage> SendAsync(HttpMethod method)
            {
                var request = new HttpRequestMessage(method, $"{ApiBase}/items/{Uri.EscapeDataString(itemId)}/description");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return await client.SendAsync(request, cancellationToken);
            }

            using var first = await SendAsync(replace ? HttpMethod.Put : HttpMethod.Post);
            if (first.IsSuccessStatusCode)
                return true;

            // Si ya existía (o no existía), intenta con el otro método.
            using var second = await SendAsync(replace ? HttpMethod.Post : HttpMethod.Put);
            if (!second.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not save description for item {ItemId}: {Body}",
                    itemId, await second.Content.ReadAsStringAsync(cancellationToken));
            }

            return second.IsSuccessStatusCode;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Could not save description for item {ItemId}.", itemId);
            return false;
        }
    }

    /// <summary>Convierte la respuesta de error de Mercado Libre en un mensaje legible.</summary>
    public static string DescribeError(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var messages = new List<string>();
            if (root.TryGetProperty("cause", out var causes) && causes.ValueKind == JsonValueKind.Array)
            {
                foreach (var cause in causes.EnumerateArray())
                {
                    if (cause.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                        messages.Add(text);
                }
            }

            if (messages.Count == 0 && root.TryGetProperty("message", out var main))
                messages.Add(main.GetString() ?? "Error desconocido");

            return messages.Count == 0 ? content : string.Join(" · ", messages.Distinct().Take(5));
        }
        catch (JsonException)
        {
            return content.Length > 500 ? content[..500] : content;
        }
    }
}
