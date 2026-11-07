using System.Text.Json;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Web.ViewModels;

namespace MarketplaceSync.Services.Products;

/// <summary>Datos del producto que usa la IA para llenar atributos.</summary>
public sealed record ProductFacts(string? Title, string? Description, string? Brand, string? Model, string? SourceUrl);

/// <summary>Lo que llenó la IA y la búsqueda sugerida para el código de barras.</summary>
public sealed record AttributeFillResult(IReadOnlyList<string> FilledIds, string? BarcodeQuery);

/// <summary>
/// Llena con IA los atributos vacíos de Mercado Libre que se deducen con seguridad del producto
/// (por ejemplo "Es bebida sin alcohol" = Sí en una bebida energética). Nunca llena el código de barras
/// ni el motivo de "no tiene código". Lo que llena queda marcado como <see cref="MercadoLibreAttributeInput.AutoFilled"/>.
/// </summary>
public sealed class AttributeAutoFillService
{
    private const string SystemPrompt = """
        Llenas atributos de publicaciones de Mercado Libre México.
        Solo pones un valor cuando se deduce con seguridad del producto (título, marca, descripción o tipo de producto).
        Si no estás seguro, omite el atributo: un dato falso hace que Mercado Libre baje la publicación.
        Nunca pongas códigos de barras, GTIN, UPC ni EAN.
        """;

    private readonly ClaudeClient _claude;
    private readonly ILogger<AttributeAutoFillService> _logger;

    public AttributeAutoFillService(ClaudeClient claude, ILogger<AttributeAutoFillService> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    public bool IsConfigured => _claude.IsConfigured;

    /// <summary>
    /// Llena los atributos vacíos de <paramref name="attributes"/> (solo los que están en <paramref name="onlyIds"/> si se da).
    /// Si <paramref name="wantBarcodeQuery"/> es true, también pide el nombre del producto en inglés para buscar su código.
    /// </summary>
    public async Task<AttributeFillResult> FillAsync(
        ProductFacts product,
        IList<MercadoLibreAttributeInput> attributes,
        bool wantBarcodeQuery,
        IReadOnlyCollection<string>? onlyIds = null,
        CancellationToken cancellationToken = default)
    {
        var empty = attributes
            .Where(a => a.Id is not (MercadoLibreListingClient.GtinId or MercadoLibreListingClient.EmptyGtinReasonId))
            .Where(a => string.IsNullOrWhiteSpace(a.ValueId) && string.IsNullOrWhiteSpace(a.ValueName))
            .Where(a => onlyIds is null || onlyIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (!_claude.IsConfigured || string.IsNullOrWhiteSpace(product.Title) || (empty.Count == 0 && !wantBarcodeQuery))
            return new AttributeFillResult(Array.Empty<string>(), FallbackQuery(product));

        var schema = empty.Select(a => new
        {
            id = a.Id,
            nombre = a.Name,
            tipo = a.ValueType,
            opciones = a.Values.Take(25).Select(v => v.Name)
        });

        var prompt = $$"""
            PRODUCTO
            - Título: {{product.Title}}
            - Marca: {{product.Brand ?? "N/D"}}
            - Modelo: {{product.Model ?? "N/D"}}
            - Descripción: {{Truncate(product.Description, 1500) ?? "N/D"}}

            ATRIBUTOS VACÍOS (llena los que puedas):
            {{JsonSerializer.Serialize(schema)}}

            REGLAS
            - Si un atributo tiene opciones, el valor debe ser EXACTAMENTE una de ellas.
            - Atributos Sí/No (tipo "boolean"): "Sí" o "No" solo si es obvio por el tipo de producto
              (bebida energética, refresco, agua o jugo → "Es bebida sin alcohol" = "Sí"; cerveza o vino → "No";
              "sin azúcar", "zero" o "ultra" en el título de una bebida → "Es sin azúcar" = "Sí").
            - Números con unidad (tipo "number_unit"): número y unidad, por ejemplo "473 mL" o "16 oz".
            - Omite lo que no sepas con seguridad.
            {{(wantBarcodeQuery ? "- 'busqueda_codigo': nombre del producto EN INGLÉS como aparece en tiendas de EE.UU., para buscar su código UPC (marca, línea, sabor, tamaño y piezas del paquete). Ej.: \"Monster Energy Zero Ultra 16 oz 24 pack\"." : "")}}

            Responde con JSON: { "atributos": { "ID": "valor" }{{(wantBarcodeQuery ? ", \"busqueda_codigo\": \"...\"" : "")}} }
            """;

        FillDto? dto;
        try
        {
            dto = await _claude.CompleteJsonAsync<FillDto>(SystemPrompt, prompt, 700, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Attribute auto-fill failed.");
            dto = null;
        }

        var filled = new List<string>();
        foreach (var attribute in empty)
        {
            if (dto?.Atributos is null || !dto.Atributos.TryGetValue(attribute.Id, out var raw))
                continue;
            var value = raw.ValueKind switch
            {
                JsonValueKind.String => raw.GetString(),
                JsonValueKind.True => "Sí",
                JsonValueKind.False => "No",
                JsonValueKind.Number => raw.GetRawText(),
                _ => null
            };
            if (Apply(attribute, value))
            {
                attribute.AutoFilled = true;
                filled.Add(attribute.Id);
            }
        }

        var query = string.IsNullOrWhiteSpace(dto?.Busqueda_Codigo) ? FallbackQuery(product) : dto!.Busqueda_Codigo!.Trim();
        return new AttributeFillResult(filled, query);
    }

    /// <summary>Pone el valor en el atributo: si tiene opciones, solo una que coincida.</summary>
    public static bool Apply(MercadoLibreAttributeInput attribute, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        value = value.Trim();

        if (attribute.Values.Count > 0)
        {
            var normalized = MercadoLibreListingClient.NormalizeName(value);
            var option = attribute.Values.FirstOrDefault(o => MercadoLibreListingClient.NormalizeName(o.Name) == normalized)
                         ?? (normalized is "si" or "yes" or "true"
                             ? attribute.Values.FirstOrDefault(o => MercadoLibreListingClient.NormalizeName(o.Name) is "si" or "yes")
                             : null);
            if (option is null)
                return false;
            attribute.ValueName = option.Name;
            attribute.ValueId = string.IsNullOrWhiteSpace(option.Id) ? $"name:{option.Name}" : option.Id;
            return true;
        }

        attribute.ValueName = value.Length > 255 ? value[..255] : value;
        attribute.ValueId = null;
        return true;
    }

    private static string? FallbackQuery(ProductFacts product)
    {
        var parts = new[] { product.Brand, product.Title }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (parts.Count == 0)
            return null;
        var text = string.Join(' ', parts);
        // Sin repetir la marca si ya está en el título.
        if (!string.IsNullOrWhiteSpace(product.Brand) && product.Title?.Contains(product.Brand, StringComparison.OrdinalIgnoreCase) == true)
            text = product.Title!;
        return text.Length > 120 ? text[..120] : text;
    }

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length > max ? text[..max] : text;

    private sealed class FillDto
    {
        public Dictionary<string, JsonElement>? Atributos { get; set; }
        public string? Busqueda_Codigo { get; set; }
    }
}
