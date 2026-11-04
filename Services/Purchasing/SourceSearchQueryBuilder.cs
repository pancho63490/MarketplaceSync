using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MarketplaceSync.Services.Purchasing;

/// <summary>
/// Convierte un título de Mercado Libre México (en español) en una búsqueda útil para eBay EE.UU.
/// Prioriza marca y números de parte/modelo, que funcionan igual en ambos idiomas.
/// Si hay una API key de Gemini configurada la usa para traducir; si falla o no existe,
/// recurre a un diccionario de términos comunes.
/// </summary>
public sealed class SourceSearchQueryBuilder
{
    private static readonly Regex TokenPattern = new(@"[\p{L}\p{N}][\p{L}\p{N}\-\/\.]*", RegexOptions.Compiled);
    private static readonly Regex PartNumberPattern = new(@"^(?=.*\d)(?=.*[a-z])[a-z0-9\-\/\.]{3,}$|^\d{4,}[a-z0-9\-]*$|^\d{2,}[\-\/]\d{2,}[a-z0-9\-]*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Dictionary = new(StringComparer.Ordinal)
    {
        // Autopartes
        ["faro"] = "headlight", ["faros"] = "headlights", ["calavera"] = "tail light", ["calaveras"] = "tail lights",
        ["espejo"] = "mirror", ["retrovisor"] = "mirror", ["bomba"] = "pump", ["agua"] = "water",
        ["gasolina"] = "fuel", ["combustible"] = "fuel", ["filtro"] = "filter", ["aceite"] = "oil",
        ["aire"] = "air", ["cabina"] = "cabin", ["balatas"] = "brake pads", ["balata"] = "brake pad",
        ["freno"] = "brake", ["frenos"] = "brakes", ["disco"] = "rotor", ["discos"] = "rotors",
        ["amortiguador"] = "shock absorber", ["amortiguadores"] = "shocks", ["bujia"] = "spark plug",
        ["bujias"] = "spark plugs", ["bobina"] = "ignition coil", ["bobinas"] = "ignition coils",
        ["sensor"] = "sensor", ["oxigeno"] = "oxygen", ["radiador"] = "radiator", ["alternador"] = "alternator",
        ["marcha"] = "starter", ["motor"] = "engine", ["banda"] = "belt", ["tensor"] = "tensioner",
        ["rotula"] = "ball joint", ["rotulas"] = "ball joints", ["horquilla"] = "control arm",
        ["horquillas"] = "control arms", ["terminal"] = "tie rod end", ["terminales"] = "tie rod ends",
        ["maza"] = "wheel hub", ["mazas"] = "wheel hubs", ["balero"] = "bearing", ["baleros"] = "bearings",
        ["llanta"] = "tire", ["llantas"] = "tires", ["rin"] = "wheel", ["rines"] = "wheels",
        ["parrilla"] = "grille", ["defensa"] = "bumper", ["cofre"] = "hood", ["puerta"] = "door",
        ["manija"] = "door handle", ["bateria"] = "battery", ["termostato"] = "thermostat",
        ["inyector"] = "fuel injector", ["inyectores"] = "fuel injectors", ["clutch"] = "clutch",
        ["embrague"] = "clutch", ["soporte"] = "mount", ["soportes"] = "mounts", ["junta"] = "gasket",
        ["empaque"] = "gasket", ["empaques"] = "gaskets", ["limpiaparabrisas"] = "wiper blades",
        ["plumas"] = "wiper blades", ["escape"] = "exhaust", ["mofle"] = "muffler", ["catalizador"] = "catalytic converter",
        ["tapete"] = "floor mat", ["tapetes"] = "floor mats", ["volante"] = "steering wheel",
        ["direccion"] = "steering", ["suspension"] = "suspension", ["ventilador"] = "fan",
        ["compresor"] = "compressor", ["condensador"] = "condenser", ["valvula"] = "valve",
        ["switch"] = "switch", ["interruptor"] = "switch", ["modulo"] = "module", ["computadora"] = "ecu",
        // Posición
        ["delantero"] = "front", ["delantera"] = "front", ["delanteros"] = "front", ["trasero"] = "rear",
        ["trasera"] = "rear", ["traseros"] = "rear", ["izquierdo"] = "left", ["izquierda"] = "left",
        ["derecho"] = "right", ["derecha"] = "right", ["superior"] = "upper", ["inferior"] = "lower",
        ["lateral"] = "side", ["piloto"] = "driver", ["copiloto"] = "passenger",
        // General
        ["cargador"] = "charger", ["inalambrico"] = "wireless", ["audifonos"] = "headphones",
        ["bocina"] = "speaker", ["bocinas"] = "speakers", ["funda"] = "case", ["reloj"] = "watch",
        ["inteligente"] = "smart", ["camara"] = "camera", ["juguete"] = "toy", ["juego"] = "set",
        ["kit"] = "kit", ["par"] = "pair", ["luz"] = "light", ["luces"] = "lights", ["led"] = "led",
        ["herramienta"] = "tool", ["herramientas"] = "tools", ["taladro"] = "drill", ["llave"] = "wrench",
        ["perfume"] = "perfume", ["crema"] = "cream", ["vitaminas"] = "vitamins", ["proteina"] = "protein",
        ["tenis"] = "sneakers", ["mochila"] = "backpack", ["cartera"] = "wallet", ["lentes"] = "glasses",
        ["consola"] = "console", ["control"] = "controller", ["teclado"] = "keyboard", ["raton"] = "mouse",
        ["monitor"] = "monitor", ["impresora"] = "printer", ["cable"] = "cable", ["adaptador"] = "adapter",
        ["original"] = "oem", ["nuevo"] = "new", ["universal"] = "universal",
        ["ceramica"] = "ceramic", ["ruido"] = "noise", ["cancelacion"] = "cancelling", ["impermeable"] = "waterproof",
        ["portatil"] = "portable", ["electrico"] = "electric", ["electrica"] = "electric", ["acero"] = "steel",
        ["aluminio"] = "aluminum", ["cuero"] = "leather", ["piel"] = "leather", ["negro"] = "black", ["blanco"] = "white",
    };

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "de", "del", "la", "el", "los", "las", "para", "con", "sin", "y", "o", "en", "por", "a", "al",
        "un", "una", "unos", "unas", "envio", "gratis", "msi", "meses", "pzs", "pz", "pieza", "piezas",
        "oferta", "promocion", "garantia", "calidad", "alta", "premium", "mx", "mexico", "compatible",
        "100", "nueva", "nuevos", "nuevas", "color", "colores", "tipo", "marca", "modelo", "envios",
    };

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SourceSearchQueryBuilder> _logger;

    public SourceSearchQueryBuilder(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<SourceSearchQueryBuilder> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> BuildAsync(
        string title,
        string? brand,
        string? model,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var translated = await TryTranslateWithGeminiAsync(apiKey, title, brand, model, cancellationToken);
            if (!string.IsNullOrWhiteSpace(translated))
                return translated;
        }

        return BuildWithRules(title, brand, model);
    }

    /// <summary>Tokens de marca y número de parte/modelo que sirven para validar la coincidencia.</summary>
    public static IReadOnlyList<string> KeyTokens(string title, string? brand, string? model)
    {
        var tokens = new List<string>();
        foreach (var token in Tokenize(model ?? string.Empty).Concat(Tokenize(title)))
        {
            if (PartNumberPattern.IsMatch(token) && !IsYear(token) && !tokens.Contains(token))
                tokens.Add(token);
        }

        return tokens.Take(4).ToList();
    }

    private static bool IsYear(string token) =>
        Regex.IsMatch(token, @"^(19|20)\d{2}(-((19|20)?\d{2}))?$");

    public static IReadOnlyList<string> Tokenize(string text)
    {
        var normalized = Normalize(text);
        return TokenPattern.Matches(normalized)
            .Select(m => m.Value.Trim('-', '/', '.'))
            .Where(t => t.Length > 0)
            .ToList();
    }

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string BuildWithRules(string title, string? brand, string? model)
    {
        var parts = new List<string>();

        void Add(string value)
        {
            foreach (var piece in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!parts.Contains(piece))
                    parts.Add(piece);
            }
        }

        if (!string.IsNullOrWhiteSpace(brand))
            Add(Normalize(brand));

        foreach (var key in KeyTokens(title, brand, model))
            Add(key);

        var titleTokens = Tokenize(title);
        var translated = new List<string>();
        var leftovers = new List<string>();
        string? year = null;

        foreach (var token in titleTokens)
        {
            if (Stopwords.Contains(token) || parts.Contains(token))
                continue;

            if (TryTranslate(token, out var english))
                translated.Add(english);
            else if (IsYear(token))
                year ??= token.Split('-')[0]; // año del vehículo: solo uno, eBay combina las palabras con AND
            else if (token.Length > 2 && !PartNumberPattern.IsMatch(token) && !Regex.IsMatch(token, @"^\d"))
                leftovers.Add(token);
        }

        // eBay exige todas las palabras: una búsqueda corta encuentra más.
        foreach (var word in translated.Take(3))
            Add(word);

        if (translated.Count == 0)
        {
            // Sin traducciones conserva las primeras palabras (suelen ser nombres propios o del vehículo).
            foreach (var word in leftovers.Take(4))
                Add(word);
        }
        else
        {
            // Nombres de vehículo/producto que no se traducen (ej. "silverado", "tacoma").
            foreach (var word in leftovers.Where(w => !LooksSpanish(w)).Take(2))
                Add(word);
        }

        if (year is not null)
            Add(year);

        var query = string.Join(' ', parts.Take(8));
        return query.Length > 100 ? query[..100] : query;
    }

    private static bool TryTranslate(string token, out string english)
    {
        if (Dictionary.TryGetValue(token, out english!))
            return true;

        // Plurales simples: "inalambricos" → "inalambrico", "delanteras" → "delantera".
        if (token.Length > 4 && token.EndsWith("es", StringComparison.Ordinal) &&
            Dictionary.TryGetValue(token[..^2], out english!))
            return true;

        if (token.Length > 3 && token.EndsWith('s') &&
            Dictionary.TryGetValue(token[..^1], out english!))
            return true;

        english = string.Empty;
        return false;
    }

    private static bool LooksSpanish(string word) =>
        word.EndsWith("cion", StringComparison.Ordinal) ||
        word.EndsWith("dor", StringComparison.Ordinal) ||
        word.EndsWith("dora", StringComparison.Ordinal) ||
        word.EndsWith("mente", StringComparison.Ordinal) ||
        word.EndsWith("ado", StringComparison.Ordinal) ||
        word.EndsWith("ada", StringComparison.Ordinal) ||
        word.EndsWith("ible", StringComparison.Ordinal) ||
        word.EndsWith("os", StringComparison.Ordinal) ||
        word.EndsWith("as", StringComparison.Ordinal) ||
        word.EndsWith("ido", StringComparison.Ordinal);

    private async Task<string?> TryTranslateWithGeminiAsync(
        string apiKey,
        string title,
        string? brand,
        string? model,
        CancellationToken cancellationToken)
    {
        try
        {
            var geminiModel = _configuration["Gemini:Model"];
            if (string.IsNullOrWhiteSpace(geminiModel))
                geminiModel = "gemini-2.5-flash";

            var prompt =
                "Convert this Mercado Libre Mexico product title into a concise English search query for eBay US " +
                "(maximum 8 words). Keep brand, model and part numbers exactly. Remove marketing words. " +
                "Reply with the query only, no quotes.\n" +
                $"Title: {title}\nBrand: {brand ?? "-"}\nModel: {model ?? "-"}";

            var payload = new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } }
                    }
                },
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0.1,
                    ["maxOutputTokens"] = 64
                }
            };

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var endpoint =
                $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(geminiModel)}:generateContent";

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Gemini query translation returned {Status}; using rules.", (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var text = document.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text))
                return null;

            var clean = Regex.Replace(text, @"[\r\n""`]+", " ").Trim();
            return clean.Length is > 2 and <= 120 ? clean : null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Gemini query translation failed; using rules.");
            return null;
        }
    }
}
