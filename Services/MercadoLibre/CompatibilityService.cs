using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Un renglón de compatibilidad: marca, modelo y rango de años (con nota opcional: motor, posición…).</summary>
public sealed class VehicleFitment
{
    public string Brand { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int YearFrom { get; set; }
    public int YearTo { get; set; }
    public string? Note { get; set; }

    /// <summary>ia o manual.</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Versiones que Mercado Libre tiene para esa marca, modelo y años (null = sin validar; 0 = no existe así).</summary>
    public int? Vehicles { get; set; }

    [JsonIgnore]
    public bool IsValid => Vehicles is > 0;

    public string Label => YearFrom == YearTo ? $"{Brand} {Model} {YearFrom}" : $"{Brand} {Model} {YearFrom}–{YearTo}";
}

/// <summary>
/// Compatibilidades de autopartes con vehículos (dominio <c>MLM-CARS_AND_VANS_FOR_COMPATIBILITIES</c>):
/// la IA sugiere, se validan contra el catálogo de Mercado Libre (count_family_products) y se mandan a la
/// publicación por familias (marca + modelo + año) solo cuando el vendedor confirma.
/// </summary>
public sealed class CompatibilityService
{
    public const string VehicleDomain = "MLM-CARS_AND_VANS_FOR_COMPATIBILITIES";
    private const int MaxFamiliesPerCall = 10;
    private const int MaxVehiclesPerCall = 200;
    private const int MaxRows = 25;
    private const int MaxYearsPerRow = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _context;
    private readonly ClaudeClient _claude;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CompatibilityService> _logger;

    public CompatibilityService(
        AppDbContext context,
        ClaudeClient claude,
        IMercadoLibreAuthService auth,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CompatibilityService> logger)
    {
        _context = context;
        _claude = claude;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static List<VehicleFitment> Read(Product product)
    {
        if (string.IsNullOrWhiteSpace(product.CompatibilityJson))
            return new List<VehicleFitment>();
        try { return JsonSerializer.Deserialize<List<VehicleFitment>>(product.CompatibilityJson, Json) ?? new(); }
        catch (JsonException) { return new List<VehicleFitment>(); }
    }

    public static void Write(Product product, IEnumerable<VehicleFitment> fitments) =>
        product.CompatibilityJson = JsonSerializer.Serialize(fitments.Take(MaxRows).ToList(), Json);

    /// <summary>Limpia los renglones capturados: sin vacíos, años en orden y dentro de un rango razonable.</summary>
    public static List<VehicleFitment> Normalize(IEnumerable<VehicleFitment> rows)
    {
        var maxYear = DateTime.UtcNow.Year + 1;
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Brand) && !string.IsNullOrWhiteSpace(r.Model) && r.YearFrom > 0)
            .Select(r =>
            {
                var from = Math.Clamp(r.YearFrom, 1950, maxYear);
                var to = Math.Clamp(r.YearTo <= 0 ? r.YearFrom : r.YearTo, 1950, maxYear);
                if (to < from) (from, to) = (to, from);
                if (to - from >= MaxYearsPerRow) to = from + MaxYearsPerRow - 1;
                return new VehicleFitment
                {
                    Brand = r.Brand.Trim(),
                    Model = r.Model.Trim(),
                    YearFrom = from,
                    YearTo = to,
                    Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim()[..Math.Min(r.Note.Trim().Length, 200)],
                    Source = r.Source,
                    Vehicles = null
                };
            })
            .Take(MaxRows)
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────
    // Sugerir con IA
    // ─────────────────────────────────────────────────────────────

    /// <summary>Pide a la IA con qué vehículos es compatible la pieza (solo lo que se sabe con seguridad).</summary>
    public async Task<(List<VehicleFitment> Rows, string? Note)> SuggestAsync(Product product, CancellationToken cancellationToken = default)
    {
        if (!_claude.IsConfigured)
            return (new List<VehicleFitment>(), "Falta la llave de la IA (Anthropic__ApiKey en Render).");

        var system = """
            Eres experto en autopartes para el mercado de México (Mercado Libre).
            Dices con qué vehículos es compatible una pieza SOLO con lo que se sabe con seguridad: lo que dice el
            título o la descripción, o la aplicación conocida de ese número de parte. Si dudas de un vehículo, no lo pongas:
            una compatibilidad falsa hace que al comprador le llegue una pieza que no le queda.
            Usa nombres de marca y modelo como aparecen en México (Chevrolet, GMC, Ford, Nissan, Volkswagen, Toyota,
            Dodge, RAM, Jeep, Honda, Mazda, Kia, Hyundai…; modelo sin la marca: "Silverado", "Jetta", "F-150").
            Une años seguidos en un solo renglón (desde–hasta). Nota corta solo si importa (motor, tracción, lado o posición).
            """;
        var prompt = $$"""
            PIEZA
            - Título: {{product.Title}}
            - Marca de la pieza: {{product.Brand ?? "N/D"}}
            - Número de parte / modelo: {{product.Model ?? "N/D"}}
            - Descripción: {{Truncate(product.Description, 2000) ?? "N/D"}}

            Responde con JSON:
            { "compatibles": [ { "marca": "Chevrolet", "modelo": "Silverado", "desde": 2014, "hasta": 2018, "nota": "motor 5.3L" } ],
              "comentario": "una línea: de dónde sale o por qué no se puede saber" }
            Si no se puede saber con seguridad, "compatibles": [].
            """;

        SuggestDto? dto;
        try { dto = await _claude.CompleteJsonAsync<SuggestDto>(system, prompt, 900, cancellationToken); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Compatibility suggestion failed for product {ProductId}.", product.Id);
            dto = null;
        }

        var rows = Normalize((dto?.Compatibles ?? new()).Select(c => new VehicleFitment
        {
            Brand = c.Marca ?? "",
            Model = c.Modelo ?? "",
            YearFrom = c.Desde ?? 0,
            YearTo = c.Hasta ?? c.Desde ?? 0,
            Note = c.Nota,
            Source = "ia"
        }));
        return (rows, dto?.Comentario);
    }

    // ─────────────────────────────────────────────────────────────
    // Validar contra el catálogo de Mercado Libre
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Revisa cada renglón contra el catálogo de vehículos. Si el modelo no existe tal cual, prueba formas más
    /// cortas ("Silverado 1500" → "Silverado") y deja la que Mercado Libre reconoce.
    /// </summary>
    public async Task ValidateAsync(Guid org, int accountId, List<VehicleFitment> rows, CancellationToken cancellationToken = default)
    {
        var token = await TokenAsync(org, accountId);
        if (token is null)
            return;

        foreach (var row in rows)
        {
            foreach (var model in ModelVariants(row.Model))
            {
                var total = 0;
                var any = false;
                for (var year = row.YearFrom; year <= row.YearTo; year++)
                {
                    var count = await CountAsync(token, row.Brand, model, year, cancellationToken);
                    if (count is null) { any = false; total = -1; break; } // la API falló: se deja sin validar
                    total += count.Value;
                    any |= count > 0;
                }
                if (total < 0) { row.Vehicles = null; break; }
                if (any)
                {
                    row.Model = model;
                    row.Vehicles = total;
                    break;
                }
                row.Vehicles = 0;
            }
        }
    }

    /// <summary>"Silverado 1500 Pickup" → ["Silverado 1500 Pickup", "Silverado 1500", "Silverado"].</summary>
    public static IEnumerable<string> ModelVariants(string model)
    {
        var words = model.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = words.Count; n >= 1; n--)
        {
            var candidate = string.Join(' ', words.Take(n));
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    private async Task<int?> CountAsync(string token, string brand, string model, int year, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["domain_id"] = VehicleDomain,
            ["attributes"] = new JsonArray
            {
                new JsonObject { ["id"] = "BRAND", ["value_name"] = brand },
                new JsonObject { ["id"] = "CAR_AND_VAN_MODEL", ["value_name"] = model },
                new JsonObject { ["id"] = "YEAR", ["value_name"] = year.ToString(CultureInfo.InvariantCulture) }
            }
        };
        var (ok, content) = await SendAsync(token, HttpMethod.Post, "/catalog_compatibilities/products_search/count_family_products", body, cancellationToken);
        if (!ok)
            return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : null;
        }
        catch (JsonException) { return null; }
    }

    // ─────────────────────────────────────────────────────────────
    // Mandar a Mercado Libre
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Manda los renglones válidos a la publicación por familias (marca + modelo + año), en llamadas de máximo
    /// 10 familias y 200 vehículos. Con <paramref name="replace"/> primero quita las que ya tenía.
    /// </summary>
    public async Task<(bool Ok, int Created, string Message)> SendToItemAsync(Guid org, int publicationId, bool replace, CancellationToken cancellationToken = default)
    {
        var publication = await _context.MarketplacePublications
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == org, cancellationToken);
        if (publication?.ExternalItemId is null || publication.MarketplaceAccountId is null)
            return (false, 0, "La publicación no está en Mercado Libre.");

        var rows = Read(publication.Product).Where(r => r.IsValid).ToList();
        if (rows.Count == 0)
            return (false, 0, "No hay compatibilidades validadas para mandar. Valídalas primero.");

        var token = await TokenAsync(org, publication.MarketplaceAccountId.Value);
        if (token is null)
            return (false, 0, "No hay sesión de Mercado Libre para esa cuenta.");

        // Familias por año con su número de versiones (para no pasar de 200 por llamada).
        var families = new List<(JsonObject Family, int Vehicles, string Label)>();
        var skipped = new List<string>();
        foreach (var row in rows)
        {
            for (var year = row.YearFrom; year <= row.YearTo; year++)
            {
                var count = await CountAsync(token, row.Brand, row.Model, year, cancellationToken) ?? 0;
                if (count == 0) continue;
                if (count > MaxVehiclesPerCall) { skipped.Add($"{row.Brand} {row.Model} {year}"); continue; }
                var family = new JsonObject
                {
                    ["domain_id"] = VehicleDomain,
                    ["attributes"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "BRAND", ["value_name"] = row.Brand },
                        new JsonObject { ["id"] = "CAR_AND_VAN_MODEL", ["value_name"] = row.Model },
                        new JsonObject { ["id"] = "YEAR", ["value_name"] = year.ToString(CultureInfo.InvariantCulture) }
                    }
                };
                if (!string.IsNullOrWhiteSpace(row.Note))
                    family["note"] = row.Note.Length > 500 ? row.Note[..500] : row.Note;
                families.Add((family, count, $"{row.Brand} {row.Model} {year}"));
            }
        }
        if (families.Count == 0)
            return (false, 0, "Mercado Libre no reconoce esos vehículos. Revisa marca y modelo.");

        var itemPath = $"/items/{Uri.EscapeDataString(publication.ExternalItemId)}/compatibilities";
        if (replace)
        {
            var (deleted, deleteContent) = await SendAsync(token, HttpMethod.Delete, itemPath, null, cancellationToken);
            if (!deleted && !deleteContent.Contains("not_found", StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("Deleting compatibilities of {Item} failed: {Body}", publication.ExternalItemId, Short(deleteContent));
        }

        var created = 0;
        var errors = new List<string>();
        foreach (var chunk in Chunk(families))
        {
            var body = new JsonObject { ["products_families"] = new JsonArray(chunk.Select(f => (JsonNode)f.Family.DeepClone()).ToArray()) };
            var (ok, content) = await SendAsync(token, HttpMethod.Post, itemPath, body, cancellationToken);
            if (ok)
            {
                try
                {
                    using var document = JsonDocument.Parse(content);
                    if (document.RootElement.TryGetProperty("created_compatibilities_count", out var c) && c.TryGetInt32(out var n))
                        created += n;
                }
                catch (JsonException) { }
            }
            else
            {
                errors.Add(MercadoLibreListingClient.DescribeError(content));
            }
        }

        if (created > 0 || errors.Count == 0)
        {
            publication.Product.CompatibilitySentAt = DateTime.UtcNow;
            publication.Product.CompatibilityCount = await CountOnItemAsync(token, publication.ExternalItemId, cancellationToken) ?? created;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var message = new StringBuilder();
        message.Append(created > 0
            ? $"Mercado Libre agregó {created} {(created == 1 ? "vehículo" : "vehículos")} compatibles."
            : errors.Count == 0 ? "Mercado Libre ya tenía esos vehículos." : "Mercado Libre no aceptó las compatibilidades.");
        if (skipped.Count > 0)
            message.Append(" Sin mandar (más de 200 versiones en un año): ").Append(string.Join(", ", skipped.Take(3))).Append('.');
        if (errors.Count > 0)
            message.Append(' ').Append(string.Join(" · ", errors.Distinct().Take(2)));
        return (errors.Count == 0 || created > 0, created, message.ToString());
    }

    /// <summary>Cuántos vehículos tiene ligados hoy la publicación en Mercado Libre (null si no se pudo leer).</summary>
    public async Task<int?> CountOnItemAsync(Guid org, int accountId, string itemId, CancellationToken cancellationToken = default)
    {
        var token = await TokenAsync(org, accountId);
        return token is null ? null : await CountOnItemAsync(token, itemId, cancellationToken);
    }

    private async Task<int?> CountOnItemAsync(string token, string itemId, CancellationToken cancellationToken)
    {
        var (ok, content) = await SendAsync(token, HttpMethod.Get, $"/items/{Uri.EscapeDataString(itemId)}/compatibilities", null, cancellationToken);
        if (!ok)
            return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var products = root.TryGetProperty("products", out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : 0;
            var catalog = root.TryGetProperty("catalog_compatibilities_count", out var c) && c.TryGetInt32(out var n) ? n : 0;
            return Math.Max(products, catalog);
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<List<(JsonObject Family, int Vehicles, string Label)>> Chunk(List<(JsonObject Family, int Vehicles, string Label)> families)
    {
        var current = new List<(JsonObject, int, string)>();
        var vehicles = 0;
        foreach (var family in families)
        {
            if (current.Count > 0 && (current.Count >= MaxFamiliesPerCall || vehicles + family.Vehicles > MaxVehiclesPerCall))
            {
                yield return current;
                current = new List<(JsonObject, int, string)>();
                vehicles = 0;
            }
            current.Add(family);
            vehicles += family.Vehicles;
        }
        if (current.Count > 0)
            yield return current;
    }

    private async Task<string?> TokenAsync(Guid org, int accountId)
    {
        try { return await _auth.GetValidAccessTokenAsync(org, accountId); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No token for compatibilities (account {AccountId}).", accountId);
            return null;
        }
    }

    private async Task<(bool Ok, string Content)> SendAsync(string token, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Compatibilities {Method} {Path} returned {Status}: {Body}", method, path, (int)response.StatusCode, Short(content));
            return (response.IsSuccessStatusCode, content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Compatibilities {Method} {Path} failed.", method, path);
            return (false, string.Empty);
        }
    }

    private static string Short(string value) => value.Length > 300 ? value[..300] : value;

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length > max ? text[..max] : text;

    private sealed class SuggestDto
    {
        public List<SuggestRow>? Compatibles { get; set; }
        public string? Comentario { get; set; }
    }

    private sealed class SuggestRow
    {
        public string? Marca { get; set; }
        public string? Modelo { get; set; }
        public int? Desde { get; set; }
        public int? Hasta { get; set; }
        public string? Nota { get; set; }
    }
}
