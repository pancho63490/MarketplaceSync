using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceSync.Web.ViewModels;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Valor elegido para un atributo de Mercado Libre (se guarda en la publicación).</summary>
public sealed record SavedAttributeValue(string Id, string? ValueId, string? ValueName);

/// <summary>Tipo de publicación que la cuenta puede usar en una categoría.</summary>
public sealed record ListingTypeOption(string Id, string Name, int? Remaining);

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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MercadoLibreListingClient> _logger;

    public MercadoLibreListingClient(
        IHttpClientFactory httpClientFactory,
        ILogger<MercadoLibreListingClient> logger,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        ApiBase = configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : "https://api.mercadolibre.com";
    }

    /// <summary>Base de la API (MercadoLibre:ApiBaseUrl solo para pruebas).</summary>
    public string ApiBase { get; }

    /// <summary>Tipo de publicación con el que se publica si no se elige otro (Premium: meses sin intereses).</summary>
    public const string DefaultListingTypeId = "gold_pro";

    public const string GtinId = "GTIN";
    public const string EmptyGtinReasonId = "EMPTY_GTIN_REASON";

    /// <summary>
    /// Atributos que hay que llenar para publicar en la categoría, con sus opciones (el recurso es público; el token es opcional).
    /// Incluye los obligatorios, los que Mercado Libre puede pedir según el producto (conditional_required /
    /// catalog_required), el código de barras (GTIN) con su razón de "no tiene", y los que se pidan en
    /// <paramref name="alsoInclude"/> por id o por nombre (por ejemplo, los que Mercado Libre reclamó al rechazar).
    /// </summary>
    public async Task<List<MercadoLibreAttributeInput>> GetRequiredAttributesAsync(
        string categoryId,
        string? accessToken,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? alsoInclude = null)
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

        var wanted = new HashSet<string>(
            (alsoInclude ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeName));

        static bool Tag(JsonNode node, string tag)
        {
            try { return node["tags"]?[tag]?.GetValue<bool>() == true; }
            catch (InvalidOperationException) { return false; }
        }

        foreach (var node in json)
        {
            if (node == null)
                continue;

            var id = node["id"]?.GetValue<string>() ?? string.Empty;
            var name = node["name"]?.GetValue<string>() ?? id;
            var required = Tag(node, "required");
            var conditional = Tag(node, "conditional_required") || Tag(node, "catalog_required") || Tag(node, "new_required");
            var asked = wanted.Contains(NormalizeName(id)) || wanted.Contains(NormalizeName(name));
            var isGtin = id is GtinId or EmptyGtinReasonId;

            if (!required && !conditional && !asked && !isGtin)
                continue;
            // Los que Mercado Libre llena solo o no deja cambiar no se muestran (salvo que los haya reclamado).
            if (!required && !asked && (Tag(node, "read_only") || Tag(node, "fixed") || Tag(node, "inferred")))
                continue;

            var valueType = node["value_type"]?.GetValue<string>();
            var input = new MercadoLibreAttributeInput
            {
                Id = id,
                Name = name,
                Required = required,
                Conditional = !required && (conditional || isGtin),
                ValueType = valueType
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

            // Un Sí/No sin opciones en el esquema se manda por nombre.
            if (input.Values.Count == 0 && string.Equals(valueType, "boolean", StringComparison.OrdinalIgnoreCase))
            {
                input.Values.Add(new MercadoLibreAttributeValueOption { Name = "Sí" });
                input.Values.Add(new MercadoLibreAttributeValueOption { Name = "No" });
            }

            result.Add(input);
        }

        // La razón de "no tiene código" solo tiene sentido junto al código.
        if (result.All(a => a.Id != GtinId))
            result.RemoveAll(a => a.Id == EmptyGtinReasonId && !a.Required);

        // Primero lo obligatorio, luego el código de barras y al final lo que puede pedir según el producto.
        return result
            .Select((attribute, index) => (attribute, index))
            .OrderBy(x => x.attribute.Required ? 0 : x.attribute.Id == GtinId ? 1 : x.attribute.Id == EmptyGtinReasonId ? 2 : 3)
            .ThenBy(x => x.index)
            .Select(x => x.attribute)
            .ToList();
    }

    /// <summary>Nombres en español de los tipos de publicación de México.</summary>
    public static string ListingTypeName(string? id) => id switch
    {
        "gold_special" => "Clásica",
        "gold_pro" => "Premium",
        "gold_premium" => "Premium",
        "gold" => "Oro",
        "silver" => "Plata",
        "bronze" => "Bronce",
        "free" => "Gratuita",
        null or "" => "sin tipo",
        _ => id
    };

    /// <summary>
    /// Tipos de publicación que esta cuenta puede usar en la categoría (GET /users/{id}/available_listing_types).
    /// Regresa null si Mercado Libre no respondió (se deja la lista fija de siempre).
    /// </summary>
    public async Task<List<ListingTypeOption>?> GetAvailableListingTypesAsync(
        string accessToken,
        string sellerId,
        string categoryId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ApiBase}/users/{Uri.EscapeDataString(sellerId)}/available_listing_types?category_id={Uri.EscapeDataString(categoryId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("available_listing_types for {Category} returned {Status}: {Body}",
                    categoryId, (int)response.StatusCode, content.Length > 300 ? content[..300] : content);
                return null;
            }

            var root = JsonNode.Parse(content);
            var list = root as JsonArray ?? root?["available"] as JsonArray;
            if (list is null)
                return null;

            var result = new List<ListingTypeOption>();
            foreach (var node in list)
            {
                var id = node?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                int? remaining = null;
                try { remaining = node?["remaining_listings"]?.GetValue<int>(); }
                catch (InvalidOperationException) { }
                catch (FormatException) { }
                if (remaining is 0)
                    continue; // ya se acabaron las de ese tipo (pasa con Gratuita)
                result.Add(new ListingTypeOption(id, ListingTypeName(id), remaining));
            }

            // Primero Premium, luego Clásica, luego el resto.
            return result
                .OrderBy(x => x.Id switch { "gold_pro" => 0, "gold_special" => 1, "free" => 3, _ => 2 })
                .ToList();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Could not read available listing types for {Category}.", categoryId);
            return null;
        }
    }

    /// <summary>Compara nombres sin acentos, mayúsculas ni signos ("¿Es bebida sin alcohol?" = "es bebida sin alcohol").</summary>
    public static string NormalizeName(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
            else if (c is ' ' or '_' && builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Deja solo dígitos y revisa que sea un código de barras real (UPC-A 12, EAN-8, EAN-13 o GTIN-14 con dígito verificador).
    /// Regresa null si es válido, o el motivo en español.
    /// </summary>
    public static string? ValidateGtin(string? raw, out string digits)
    {
        digits = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return "Escribe o escanea el código de barras.";
        if (raw!.Any(c => char.IsLetter(c)))
            return "El código de barras solo lleva números.";
        if (digits.Length is not (8 or 12 or 13 or 14))
            return $"El código de barras tiene {digits.Length} dígitos; debe tener 8, 12, 13 o 14. Revísalo en la etiqueta.";

        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var position = digits.Length - 1 - i; // 1 = el dígito junto al verificador
            sum += (digits[i] - '0') * (position % 2 == 1 ? 3 : 1);
        }
        var check = (10 - sum % 10) % 10;
        return check == digits[^1] - '0'
            ? null
            : "Ese código de barras no es válido (el último dígito no cuadra). Vuelve a escanearlo o cópialo con cuidado.";
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

    /// <summary>Lo que Mercado Libre dijo que falta: ids entre corchetes y nombres entre comillas.</summary>
    public sealed record MissingAttributes(IReadOnlyList<string> Ids, IReadOnlyList<string> Names)
    {
        public bool Any => Ids.Count > 0 || Names.Count > 0;
        public IEnumerable<string> All => Ids.Concat(Names);
    }

    private static readonly System.Text.RegularExpressions.Regex MissingIdsPattern = new(
        @"attributes?\s*\[([A-Z0-9_,\s]+)\]\s*(?:is|are)\s*(?:required|missing|mandatory)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex MissingNamePattern = new(
        @"(?:campo|atributo|attribute)\s*[""“«']([^""”»']{2,80})[""”»']\s*(?:es obligatorio|is required|is mandatory|no est)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static MissingAttributes ParseMissingAttributes(string content)
    {
        var ids = new List<string>();
        var names = new List<string>();
        foreach (var (_, message) in ReadCauses(content))
        {
            foreach (System.Text.RegularExpressions.Match match in MissingIdsPattern.Matches(message))
            {
                ids.AddRange(match.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !x.StartsWith("MLM", StringComparison.OrdinalIgnoreCase)));
            }

            foreach (System.Text.RegularExpressions.Match match in MissingNamePattern.Matches(message))
                names.Add(match.Groups[1].Value.Trim());
        }

        return new MissingAttributes(
            ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Errores (no avisos) que trae la respuesta de Mercado Libre: (code, message).</summary>
    private static List<(string Code, string Message)> ReadCauses(string content)
    {
        var errors = new List<(string, string)>();
        var warnings = new List<(string, string)>();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return errors;

            if (root.TryGetProperty("cause", out var causes) && causes.ValueKind == JsonValueKind.Array)
            {
                foreach (var cause in causes.EnumerateArray())
                {
                    if (cause.ValueKind != JsonValueKind.Object ||
                        !cause.TryGetProperty("message", out var message) ||
                        message.GetString() is not { Length: > 0 } text)
                        continue;

                    var code = cause.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? "" : "";
                    var type = cause.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    (string.Equals(type, "warning", StringComparison.OrdinalIgnoreCase) ? warnings : errors).Add((code, text));
                }
            }

            if (errors.Count == 0 && root.TryGetProperty("message", out var main) && main.GetString() is { Length: > 0 } mainText)
                errors.Add((root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "", mainText));
        }
        catch (JsonException)
        {
        }

        return errors.Count > 0 ? errors : warnings;
    }

    private static readonly Dictionary<string, string> KnownAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [GtinId] = "Código de barras (GTIN)",
        [EmptyGtinReasonId] = "Motivo de no tener código de barras",
        ["BRAND"] = "Marca",
        ["MODEL"] = "Modelo",
        ["PART_NUMBER"] = "Número de parte",
        ["MPN"] = "Número de parte (MPN)",
        ["ITEM_CONDITION"] = "Condición",
        ["SELLER_SKU"] = "SKU",
        ["NET_VOLUME"] = "Volumen neto",
        ["UNITS_PER_PACK"] = "Unidades por paquete"
    };

    /// <summary>
    /// Convierte la respuesta de error de Mercado Libre en un mensaje legible en español.
    /// <paramref name="names"/> traduce ids de atributo a su nombre (del esquema de la categoría).
    /// </summary>
    public static string DescribeError(string content, IReadOnlyDictionary<string, string>? names = null)
    {
        var causes = ReadCauses(content);
        if (causes.Count == 0)
            return content.Length > 500 ? content[..500] : content;

        string NameOf(string id) =>
            id is GtinId or EmptyGtinReasonId ? KnownAttributeNames[id]
            : names != null && names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n
            : KnownAttributeNames.TryGetValue(id, out var k) ? k
            : id;

        var messages = new List<string>();
        foreach (var (code, message) in causes)
        {
            var text = message.Trim();
            var lower = (code + " " + text).ToLowerInvariant();

            var missingIds = MissingIdsPattern.Match(text);
            var missingName = MissingNamePattern.Match(text);
            if (missingIds.Success)
            {
                var ids = missingIds.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !x.StartsWith("MLM", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var label = string.Join(", ", ids.Select(NameOf));
                var gtinHint = ids.Contains(GtinId, StringComparer.OrdinalIgnoreCase)
                    ? " Escanéalo de la etiqueta o, si el producto no tiene, elige el motivo en “Sin código de barras”."
                    : "";
                messages.Add($"Falta llenar: {label}.{gtinHint}");
            }
            else if (missingName.Success)
            {
                messages.Add($"Falta llenar: {missingName.Groups[1].Value.Trim()}.");
            }
            else if (lower.Contains("gtin") && (lower.Contains("invalid") || lower.Contains("inválid") || lower.Contains("not valid")))
            {
                messages.Add("Mercado Libre no reconoce ese código de barras. Revisa que sea el de la etiqueta del producto (no el de la caja de envío).");
            }
            else if (lower.Contains("title") && (lower.Contains("length") || lower.Contains("long")))
            {
                messages.Add("El título es muy largo: máximo 60 caracteres.");
            }
            else if (lower.Contains("picture") || lower.Contains("image"))
            {
                messages.Add("Mercado Libre no pudo usar la imagen. Revisa que el enlace abra una foto (JPG o PNG) de al menos 500 px.");
            }
            else if (lower.Contains("price") && (lower.Contains("minimum") || lower.Contains("min_") || lower.Contains("lower")))
            {
                messages.Add("El precio está por debajo del mínimo que acepta esta categoría.");
            }
            else if (lower.Contains("seller.unable_to_list") || lower.Contains("unable to list") || lower.Contains("seller.forbidden"))
            {
                messages.Add($"Tu cuenta de Mercado Libre no puede publicar ahora. Revisa avisos o datos pendientes en tu cuenta. (Mercado Libre: {text})");
            }
            else if (lower.Contains("listing_type") || lower.Contains("listing type"))
            {
                // Se deja el texto original: este error tiene muchas causas (tipo, cupo de gratuitas, cuenta nueva…).
                messages.Add($"Mercado Libre no aceptó el tipo de publicación: {text}");
            }
            else if (lower.Contains("category") && (lower.Contains("not a leaf") || lower.Contains("leaf") || lower.Contains("invalid")))
            {
                messages.Add("La categoría no sirve para publicar: elige la más específica (la última del árbol).");
            }
            else if (lower.Contains("attribute") && (lower.Contains("invalid") || lower.Contains("not valid")))
            {
                var bracket = System.Text.RegularExpressions.Regex.Match(text, @"\[([A-Z0-9_]+)\]");
                messages.Add(bracket.Success
                    ? $"El valor de “{NameOf(bracket.Groups[1].Value)}” no es válido para Mercado Libre."
                    : $"Un atributo tiene un valor que Mercado Libre no acepta ({text}).");
            }
            else
            {
                messages.Add(text);
            }
        }

        return string.Join(" · ", messages.Distinct().Take(5));
    }
}
