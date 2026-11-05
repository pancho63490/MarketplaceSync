using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceSync.Services.Interfaces;

namespace MarketplaceSync.Services.Ai;

public sealed class ClaudeProductAiEnricherService : IProductAiEnricherService
{
    private const string SystemPrompt =
        "Eres un experto en eCommerce y autopartes para Mercado Libre México. " +
        "Redactas fichas de producto claras, en español de México, que cumplen las políticas de Mercado Libre. " +
        "Nunca inventas especificaciones, compatibilidades ni números de parte que no estén en los datos; si falta un dato, lo omites.";

    private static readonly Regex ProhibitedWordsPattern = new(
        @"\b(oferta|envio gratis|gratis|descuento|remate|ganga|garantizado|el mejor|barato)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ClaudeClient _claude;
    private readonly ILogger<ClaudeProductAiEnricherService> _logger;

    public ClaudeProductAiEnricherService(
        ClaudeClient claude,
        ILogger<ClaudeProductAiEnricherService> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    public async Task<ProductAiEnrichmentResult> EnrichProductAsync(
        ProductAiEnrichmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_claude.IsConfigured)
        {
            var aiResult = await CallClaudeAsync(request, cancellationToken);
            if (aiResult is not null)
                return aiResult;

            _logger.LogWarning(
                "Claude enrichment returned no usable result for product '{Title}'. Falling back to local heuristic.",
                request.RawTitle);
        }

        return FallbackEnrichment(request);
    }

    private async Task<ProductAiEnrichmentResult?> CallClaudeAsync(
        ProductAiEnrichmentRequest request,
        CancellationToken cancellationToken)
    {
        var schemaSummary = request.RequiredAttributes.Select(attr => new
        {
            attr.Id,
            attr.Name,
            attr.Required,
            attr.ValueType,
            AllowedValues = attr.AllowedValues.Take(15).Select(v => new { v.Id, v.Name })
        });

        var prompt = $$"""
            Tu misión es transformar este producto importado de {{request.SourceMarketplace ?? "EE.UU."}} para publicarlo automáticamente en Mercado Libre México (Categoría {{request.CategoryId}} - {{request.CategoryName ?? "General"}}).

            DATOS CRUDOS DEL PRODUCTO:
            - Título original: {{request.RawTitle}}
            - Marca detectada: {{request.RawBrand ?? "No especificada"}}
            - Modelo detectado: {{request.RawModel ?? "No especificada"}}
            - Descripción original: {{request.RawDescription ?? "Sin descripción"}}
            - Precio origen USD: {{(request.SourcePriceUsd.HasValue ? $"${request.SourcePriceUsd.Value:N2} USD" : "N/D")}}
            - URL origen: {{request.SourceUrl ?? "N/D"}}

            ESQUEMA DE ATRIBUTOS REQUERIDOS POR MERCADO LIBRE:
            {{JsonSerializer.Serialize(schemaSummary)}}

            REGLAS ESTRICTAS DE PUBLICACIÓN EN MERCADO LIBRE:
            0. IDIOMA: todo en español de México. Si el título o la descripción original están en inglés, TRADÚCELOS; no dejes palabras en inglés salvo marca, modelo, número de parte y nombres de vehículos. Usa términos mexicanos (balatas, maza, balero, rótula, calavera, defensa, cofre, salpicadera, mofle, marcha, banda, lado piloto / lado copiloto).
            1. 'optimizedTitle': EXACTAMENTE máximo 60 caracteres. Formato: [Tipo de producto/pieza] + [Marca] + [Modelo o aplicación] + [Año o especificación clave]. NUNCA uses palabras promocionales (ej. oferta, gratis, garantia, original).
            2. 'cleanDescription': Formato en TEXTO PLANO legible con viñetas con guiones (MeLi no admite HTML ni Markdown complejo).
               Incluye:
               - DESCRIPCIÓN GENERAL
               - COMPATIBILIDAD Y APLICACIÓN
               - ESPECIFICACIONES TÉCNICAS
               - CONTENIDO DEL PAQUETE
               - INFORMACIÓN IMPORTANTE
               ELIMINA cualquier mención de Amazon, Prime, eBay, AliExpress, envíos de EE.UU. o garantías extranjeras.
            3. 'brand': Nombre de la marca de la autoparte o 'Genérica'.
            4. 'model': Modelo o número de parte principal.
            5. 'attributes': Diccionario cuya clave es el Id de cada atributo requerido (ej. "BRAND", "PART_NUMBER").
               Si el atributo tiene AllowedValues, el valor debe ser EXACTAMENTE uno de esos nombres.
               Si no puedes saber el valor con los datos dados, OMITE ese atributo (no inventes, no pongas "No aplica").
            6. 'highRiskIntellectualProperty': true si parece una réplica no autorizada o violación de marca; false si es refacción aftermarket legítima.
            7. 'confidenceScore': 0 a 100 de acuerdo a la completitud de los datos.

            RESPONDE CON UN OBJETO JSON CON ESTA ESTRUCTURA:
            {
              "optimizedTitle": "...",
              "cleanDescription": "...",
              "brand": "...",
              "model": "...",
              "attributes": { "BRAND": "...", "PART_NUMBER": "..." },
              "highRiskIntellectualProperty": false,
              "intellectualPropertyWarning": null,
              "confidenceScore": 95
            }
            """;

        var parsed = await _claude.CompleteJsonAsync<EnrichmentDto>(
            SystemPrompt,
            prompt,
            2000,
            cancellationToken);

        if (parsed is null)
            return null;

        var title = SanitizeTitle(parsed.OptimizedTitle, request.RawTitle);
        if (ProductTitleTranslator.LooksEnglish(title))
            title = SanitizeTitle(ProductTitleTranslator.Translate(title), request.RawTitle);
        var description = CleanDescriptionText(parsed.CleanDescription);

        return new ProductAiEnrichmentResult(
            title,
            description,
            string.IsNullOrWhiteSpace(parsed.Brand) ? request.RawBrand : parsed.Brand,
            string.IsNullOrWhiteSpace(parsed.Model) ? request.RawModel : parsed.Model,
            ToStringDictionary(parsed.Attributes),
            parsed.HighRiskIntellectualProperty,
            parsed.IntellectualPropertyWarning,
            Math.Clamp(parsed.ConfidenceScore, 10, 100),
            $"Claude ({_claude.Model})");
    }

    private static IReadOnlyDictionary<string, string> ToStringDictionary(
        Dictionary<string, JsonElement>? attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attributes is null)
            return result;

        foreach (var (key, value) in attributes)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(text))
                result[key] = text.Trim();
        }

        return result;
    }

    private static ProductAiEnrichmentResult FallbackEnrichment(ProductAiEnrichmentRequest request)
    {
        var spanishTitle = ProductTitleTranslator.LooksEnglish(request.RawTitle)
            ? ProductTitleTranslator.Translate(request.RawTitle)
            : request.RawTitle;
        var title = SanitizeTitle(spanishTitle, spanishTitle);
        var brand = !string.IsNullOrWhiteSpace(request.RawBrand)
            ? request.RawBrand.Trim()
            : "Genérica";
        var model = !string.IsNullOrWhiteSpace(request.RawModel)
            ? request.RawModel.Trim()
            : null;

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BRAND"] = brand,
            ["CONDITION"] = "Nuevo"
        };

        if (!string.IsNullOrWhiteSpace(model))
            attributes["MODEL"] = model;

        // Sin IA no se adivinan atributos: elegir la primera opción de una lista publicaría datos falsos.
        // Solo se completan los que se conocen con certeza; el resto queda pendiente para revisión.
        foreach (var req in request.RequiredAttributes)
        {
            if (attributes.ContainsKey(req.Id))
                continue;

            if ((req.Id.Equals("PART_NUMBER", StringComparison.OrdinalIgnoreCase) ||
                 req.Id.Equals("MPN", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(request.RawModel))
                attributes[req.Id] = request.RawModel.Trim();
        }

        var description = BuildFallbackDescription(request with { RawTitle = title }, brand, model);

        return new ProductAiEnrichmentResult(
            title,
            description,
            brand,
            model,
            attributes,
            false,
            null,
            75,
            "RuleEngine-Fallback");
    }

    private static string SanitizeTitle(string? title, string defaultTitle)
    {
        var raw = string.IsNullOrWhiteSpace(title) ? defaultTitle : title;
        raw = ProhibitedWordsPattern.Replace(raw, " ").Trim();
        raw = Regex.Replace(raw, @"\s+", " ");

        if (raw.Length <= 60)
            return raw;

        var truncated = raw[..60];
        var lastSpace = truncated.LastIndexOf(' ');
        return lastSpace > 20 ? truncated[..lastSpace].Trim() : truncated.Trim();
    }

    private static string CleanDescriptionText(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "Producto importado de alta calidad para refacción automotriz. Verifica compatibilidad antes de comprar.";

        var cleaned = Regex.Replace(description, @"<[^>]*>", " ");
        cleaned = Regex.Replace(cleaned, @"\b(amazon|prime|ebay|aliexpress|dropshipping)\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(cleaned, @"\n{3,}", "\n\n").Trim();
    }

    private static string BuildFallbackDescription(
        ProductAiEnrichmentRequest request,
        string brand,
        string? model)
    {
        return $"""
            DESCRIPCIÓN GENERAL:
            {request.RawTitle}

            ESPECIFICACIONES:
            - Marca: {brand}
            - Modelo / Parte: {model ?? "Consulta el número de parte en la foto"}
            - Condición: Nuevo
            - Origen: Importado para venta en México

            COMPATIBILIDAD:
            Revisa las especificaciones y número de parte con tu mecánico o manual de usuario para confirmar compatibilidad antes de comprar.

            ENVÍO Y ATENCIÓN:
            Producto listo para envío a todo México mediante paquetería de Mercado Envíos.
            """.Trim();
    }

    private sealed class EnrichmentDto
    {
        public string? OptimizedTitle { get; set; }
        public string? CleanDescription { get; set; }
        public string? Brand { get; set; }
        public string? Model { get; set; }
        public Dictionary<string, JsonElement>? Attributes { get; set; }
        public bool HighRiskIntellectualProperty { get; set; }
        public string? IntellectualPropertyWarning { get; set; }
        public int ConfidenceScore { get; set; }
    }
}
