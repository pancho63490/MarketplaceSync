using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceSync.Services.Interfaces;

namespace MarketplaceSync.Services.Ai;

public sealed class GeminiProductAiEnricherService : IProductAiEnricherService
{
    private static readonly Regex ProhibitedWordsPattern = new(
        @"\b(oferta|envio gratis|gratis|descuento|remate|ganga|garantizado|el mejor|barato)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiProductAiEnricherService> _logger;

    public GeminiProductAiEnricherService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GeminiProductAiEnricherService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ProductAiEnrichmentResult> EnrichProductAsync(
        ProductAiEnrichmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ResolveApiKey();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var aiResult = await CallGeminiAsync(apiKey, request, cancellationToken);
                if (aiResult is not null)
                    return aiResult;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Gemini AI enrichment failed for product '{Title}'. Falling back to local heuristic.",
                    request.RawTitle);
            }
        }

        return FallbackEnrichment(request);
    }

    private string? ResolveApiKey()
    {
        var key = _configuration["Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(key))
            return key;

        key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            return key;

        return Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
    }

    private async Task<ProductAiEnrichmentResult?> CallGeminiAsync(
        string apiKey,
        ProductAiEnrichmentRequest request,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={Uri.EscapeDataString(apiKey)}";

        var schemaSummary = request.RequiredAttributes.Select(attr => new
        {
            attr.Id,
            attr.Name,
            attr.Required,
            attr.ValueType,
            AllowedValues = attr.AllowedValues.Take(15).Select(v => new { v.Id, v.Name })
        });

        var prompt = $$"""
            Eres un experto en eCommerce y autopartes para Mercado Libre México.
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
            5. 'attributes': Diccionario con los atributos requeridos asignados con el valor idóneo.
            6. 'highRiskIntellectualProperty': true si parece una réplica no autorizada o violación de marca; false si es refacción aftermarket legítima.
            7. 'confidenceScore': 0 a 100 de acuerdo a la completitud de los datos.

            RESPONDE ÚNICAMENTE CON EL OBJETO JSON VÁLIDO CON ESTA ESTRUCTURA:
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

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                response_mime_type = "application/json"
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Gemini API returned HTTP {StatusCode}: {ErrorBody}",
                (int)response.StatusCode,
                errorBody);
            return null;
        }

        var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(jsonResponse);

        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
            return null;

        var text = candidates[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parsed = JsonSerializer.Deserialize<GeminiEnrichmentDto>(
            text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (parsed is null)
            return null;

        var title = SanitizeTitle(parsed.OptimizedTitle, request.RawTitle);
        var description = CleanDescriptionText(parsed.CleanDescription);

        return new ProductAiEnrichmentResult(
            title,
            description,
            parsed.Brand ?? request.RawBrand,
            parsed.Model ?? request.RawModel,
            parsed.Attributes ?? new Dictionary<string, string>(),
            parsed.HighRiskIntellectualProperty,
            parsed.IntellectualPropertyWarning,
            Math.Clamp(parsed.ConfidenceScore, 10, 100),
            "Gemini-1.5-Flash");
    }

    private static ProductAiEnrichmentResult FallbackEnrichment(ProductAiEnrichmentRequest request)
    {
        var title = SanitizeTitle(request.RawTitle, request.RawTitle);
        var brand = !string.IsNullOrWhiteSpace(request.RawBrand)
            ? request.RawBrand.Trim()
            : "Genérica";
        var model = !string.IsNullOrWhiteSpace(request.RawModel)
            ? request.RawModel.Trim()
            : "Estándar";

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BRAND"] = brand,
            ["MODEL"] = model,
            ["CONDITION"] = "Nuevo"
        };

        foreach (var req in request.RequiredAttributes)
        {
            if (attributes.ContainsKey(req.Id))
                continue;

            if (req.AllowedValues.Count > 0)
            {
                attributes[req.Id] = req.AllowedValues[0].Name;
            }
            else
            {
                attributes[req.Id] = req.Id.Equals("PART_NUMBER", StringComparison.OrdinalIgnoreCase)
                    ? model
                    : "No aplica";
            }
        }

        var description = BuildFallbackDescription(request, brand, model);

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
        string model)
    {
        return $"""
            DESCRIPCIÓN GENERAL:
            {request.RawTitle}

            ESPECIFICACIONES:
            - Marca: {brand}
            - Modelo / Parte: {model}
            - Condición: Nuevo
            - Origen: Importado para venta en México

            COMPATIBILIDAD:
            Revisa las especificaciones y número de parte con tu mecánico o manual de usuario para confirmar compatibilidad antes de comprar.

            ENVÍO Y ATENCIÓN:
            Producto listo para envío a todo México mediante paquetería de Mercado Envíos.
            """.Trim();
    }

    private sealed class GeminiEnrichmentDto
    {
        public string? OptimizedTitle { get; set; }
        public string? CleanDescription { get; set; }
        public string? Brand { get; set; }
        public string? Model { get; set; }
        public Dictionary<string, string>? Attributes { get; set; }
        public bool HighRiskIntellectualProperty { get; set; }
        public string? IntellectualPropertyWarning { get; set; }
        public int ConfidenceScore { get; set; }
    }
}
