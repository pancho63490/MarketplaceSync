using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceSync.Services.MercadoLibre;

namespace MarketplaceSync.Services.Products;

/// <summary>Código de barras encontrado en internet con lo que dice la base de datos.</summary>
public sealed record BarcodeCandidate(string Gtin, string Title, string? Brand, string? ImageUrl, double Score, bool Strong);

/// <summary>
/// Busca el código UPC/EAN de un producto en UPCitemdb (base pública de códigos de EE.UU.).
/// Solo regresa códigos válidos. Un candidato es "seguro" (<see cref="BarcodeCandidate.Strong"/>) cuando
/// coincide la marca, todos los números (tamaño, piezas del paquete) y la mayoría de las palabras:
/// la lata suelta y el paquete de 24 tienen códigos distintos.
/// Configuración: UpcItemDb:UserKey (opcional, plan de pago) y UpcItemDb:BaseUrl (solo pruebas).
/// Sin llave se usa el plan gratuito (unas 100 búsquedas al día).
/// </summary>
public sealed class BarcodeLookupService
{
    private static readonly Regex Tokenizer = new(@"[a-z]+|\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "for", "of", "pack", "count", "ct", "oz", "fl", "can", "cans", "bottle", "bottles",
        "de", "con", "para", "sin", "el", "la", "los", "las", "paquete", "piezas", "pzas", "lata", "latas", "new", "nuevo"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BarcodeLookupService> _logger;

    public BarcodeLookupService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<BarcodeLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public const string OriginName = "internet (UPCitemdb)";

    public async Task<List<BarcodeCandidate>> SearchAsync(string? query, string? brand, CancellationToken cancellationToken = default)
    {
        var result = new List<BarcodeCandidate>();
        if (string.IsNullOrWhiteSpace(query))
            return result;

        var userKey = _configuration["UpcItemDb:UserKey"];
        var baseUrl = _configuration["UpcItemDb:BaseUrl"] is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : "https://api.upcitemdb.com";
        var path = string.IsNullOrWhiteSpace(userKey) ? "/prod/trial/search" : "/prod/v1/search";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}{path}?s={Uri.EscapeDataString(query.Trim())}&match_mode=0&type=product");
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(userKey))
            {
                request.Headers.Add("user_key", userKey);
                request.Headers.Add("key_type", "3scale");
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("UPCitemdb search returned {Status}: {Body}", (int)response.StatusCode,
                    content.Length > 200 ? content[..200] : content);
                return result;
            }

            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in items.EnumerateArray())
            {
                var title = Str(item, "title");
                var code = Str(item, "upc") ?? Str(item, "ean");
                if (string.IsNullOrWhiteSpace(title) || MercadoLibreListingClient.ValidateGtin(code, out var digits) is not null)
                    continue;
                // Un EAN-13 que empieza con 0 es el mismo UPC-A de 12 dígitos.
                if (digits.Length == 13 && digits[0] == '0')
                    digits = digits[1..];

                var itemBrand = Str(item, "brand");
                string? image = null;
                if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                    image = images.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true);

                var (score, strong) = Match(query, brand, title, itemBrand);
                if (result.Any(x => x.Gtin == digits))
                    continue;
                result.Add(new BarcodeCandidate(digits, title.Trim(), itemBrand, image, score, strong));
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "UPCitemdb search failed for {Query}.", query);
        }

        return result.OrderByDescending(x => x.Strong).ThenByDescending(x => x.Score).Take(5).ToList();
    }

    /// <summary>
    /// Qué tanto se parece el producto encontrado a lo que se busca.
    /// Seguro = misma marca + todos los números de la búsqueda + al menos 60% de las palabras.
    /// </summary>
    public static (double Score, bool Strong) Match(string query, string? brand, string candidateTitle, string? candidateBrand)
    {
        var queryTokens = Tokens(query);
        var candidateText = Normalize(candidateTitle + " " + candidateBrand);
        var candidateTokens = Tokens(candidateTitle + " " + candidateBrand);

        var words = queryTokens.Where(t => !char.IsDigit(t[0]) && t.Length >= 3 && !StopWords.Contains(t)).Distinct().ToList();
        var numbers = queryTokens.Where(t => char.IsDigit(t[0])).Distinct().ToList();

        var wordHits = words.Count(w => candidateTokens.Contains(w));
        var score = words.Count == 0 ? 0 : (double)wordHits / words.Count;
        var numbersOk = numbers.All(n => candidateTokens.Contains(n));

        var brandOk = string.IsNullOrWhiteSpace(brand) ||
                      Tokens(brand).Where(t => t.Length >= 3).All(t => candidateTokens.Contains(t)) ||
                      candidateText.Contains(Normalize(brand), StringComparison.Ordinal);

        if (!numbersOk)
            score *= 0.5;
        return (Math.Round(score, 2), brandOk && numbersOk && score >= 0.6 && words.Count >= 2);
    }

    private static List<string> Tokens(string? text) =>
        Tokenizer.Matches(Normalize(text)).Select(m => m.Value).ToList();

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString();
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
