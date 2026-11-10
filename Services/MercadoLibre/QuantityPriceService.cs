using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Un tramo: desde <see cref="MinUnits"/> piezas, cada una a <see cref="Amount"/>.</summary>
public sealed class QuantityTier
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("min")] public int MinUnits { get; set; }
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
}

public sealed class QuantityPricePanel
{
    public required MarketplacePublication Publication { get; init; }
    public List<QuantityTier> Tiers { get; init; } = new();
    /// <summary>Precio por pieza más bajo que deja tu ganancia mínima.</summary>
    public decimal? Floor { get; init; }
    /// <summary>Tramos que propone la app (2, 3 y 5 piezas) sin bajar del mínimo.</summary>
    public List<QuantityTier> Suggested { get; init; } = new();
    public string? Unavailable { get; init; }
}

/// <summary>
/// Precio por cantidad de Mercado Libre (<c>/items/{id}/prices/standard/quantity</c>): descuento por llevar varias piezas.
/// Hoy Mercado Libre lo muestra a compradores con cuenta de negocio y lo activa a vendedores seleccionados.
/// La app no deja poner un precio por pieza abajo de tu ganancia mínima.
/// </summary>
public sealed class QuantityPriceService
{
    public const int MaxTiers = 5;
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");
    private static readonly (int Units, decimal Discount)[] DefaultSteps = { (2, 0.03m), (3, 0.05m), (5, 0.08m) };

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly AutoPriceService _autoPrice;
    private readonly ICrossBorderPricingService _pricing;
    private readonly ILogger<QuantityPriceService> _logger;

    public QuantityPriceService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory, IConfiguration configuration,
        AutoPriceService autoPrice, ICrossBorderPricingService pricing, ILogger<QuantityPriceService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _autoPrice = autoPrice;
        _pricing = pricing;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static List<QuantityTier> Read(MarketplacePublication publication)
    {
        if (string.IsNullOrWhiteSpace(publication.QuantityPricesJson)) return new();
        try { return JsonSerializer.Deserialize<List<QuantityTier>>(publication.QuantityPricesJson) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static string ErrorLabel(string? code, HttpStatusCode status) => code switch
    {
        _ when status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
            "Mercado Libre no tiene activado el precio por cantidad para tu cuenta (lo activa a vendedores seleccionados).",
        "invalid.price_per_quantity" => "Hay dos tramos con la misma cantidad; cada tramo debe tener una cantidad distinta.",
        "invalid.price.currency" => "La moneda no coincide con la de la publicación.",
        "bad.request" => "Mercado Libre rechazó los tramos: revisa cantidades (2 o más) y que sean máximo 5.",
        "not.found" => "Mercado Libre no encontró la publicación.",
        _ => "Mercado Libre no respondió. Intenta de nuevo en un rato."
    };

    public async Task<QuantityPricePanel?> PanelAsync(Guid org, int publicationId, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication is null) return null;
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var floor = _autoPrice.Floor(publication, settings);
        var token = await TokenAsync(org, publication, cancellationToken);
        string? unavailable = null;
        if (token is null)
            unavailable = "No hay sesión de Mercado Libre para esta publicación.";
        else
        {
            var (ok, status, content) = await SendAsync(HttpMethod.Get, $"/items/{Uri.EscapeDataString(publication.ExternalItemId!)}/prices", token, null, cancellationToken);
            if (ok) ApplyPrices(publication, content);
            else if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) unavailable = ErrorLabel(null, status);
            await _context.SaveChangesAsync(cancellationToken);
        }
        return new QuantityPricePanel
        {
            Publication = publication,
            Tiers = Read(publication),
            Floor = floor,
            Suggested = Suggest(publication.Price, floor),
            Unavailable = unavailable
        };
    }

    /// <summary>2, 3 y 5 piezas con 3%, 5% y 8% menos, sin bajar del mínimo (los que no caben se quitan).</summary>
    public static List<QuantityTier> Suggest(decimal? price, decimal? floor)
    {
        if (price is not > 0 || floor is not > 0) return new();
        return DefaultSteps
            .Select(s => new QuantityTier { MinUnits = s.Units, Amount = Math.Floor(price.Value * (1 - s.Discount)) })
            .Where(t => t.Amount >= floor.Value && t.Amount < price.Value)
            .ToList();
    }

    /// <summary>Reemplaza los tramos. Lista vacía = quitar el precio por cantidad.</summary>
    public async Task<(bool Ok, string Message)> SaveAsync(Guid org, int publicationId, IReadOnlyList<(int Units, decimal Amount)> tiers, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication?.ExternalItemId is null) return (false, "No encontré esa publicación.");
        var clean = tiers.Where(t => t.Units > 0 || t.Amount > 0).OrderBy(t => t.Units).ToList();
        if (clean.Count > MaxTiers) return (false, $"Máximo {MaxTiers} tramos.");
        if (clean.Any(t => t.Units < 2)) return (false, "Cada tramo es desde 2 piezas o más.");
        if (clean.Select(t => t.Units).Distinct().Count() != clean.Count) return (false, "Cada tramo debe tener una cantidad distinta.");
        if (clean.Any(t => t.Amount <= 0)) return (false, "Escribe el precio por pieza de cada tramo.");
        if (publication.Price is > 0 && clean.Any(t => t.Amount >= publication.Price)) return (false, "El precio por cantidad debe ser menor al precio normal.");
        if (clean.Count > 0)
        {
            var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
            var floor = _autoPrice.Floor(publication, settings);
            if (floor is null) return (false, "Primero pon el costo del producto: sin costo no sé cuál es tu precio mínimo.");
            if (clean.FirstOrDefault(t => t.Amount < floor.Value) is { Units: > 0 } low)
                return (false, $"A {low.Amount.ToString("C0", Mx)} por pieza no ganas lo mínimo; el precio más bajo puede ser {floor.Value.ToString("C0", Mx)}.");
        }
        for (var i = 1; i < clean.Count; i++)
            if (clean[i].Amount >= clean[i - 1].Amount) return (false, "El precio por pieza debe bajar cuando sube la cantidad.");

        var token = await TokenAsync(org, publication, cancellationToken);
        if (token is null) return (false, "No hay sesión de Mercado Libre para esta publicación.");
        var currency = string.IsNullOrWhiteSpace(publication.CurrencyId) ? "MXN" : publication.CurrencyId;
        // Los tramos que no se mandan se borran: se mandan solo los nuevos.
        var body = new
        {
            prices = clean.Select(t => new
            {
                amount = t.Amount,
                currency_id = currency,
                conditions = new { context_restrictions = new[] { "channel_marketplace", "user_type_business" }, min_purchase_unit = t.Units }
            }).ToList()
        };
        var (ok, status, content) = await SendAsync(HttpMethod.Post, $"/items/{Uri.EscapeDataString(publication.ExternalItemId)}/prices/standard/quantity", token, body, cancellationToken);
        if (!ok) return (false, ErrorLabel(ErrorCode(content), status));
        ApplyPrices(publication, content);
        if (clean.Count == 0) publication.QuantityPricesJson = null;
        await _context.SaveChangesAsync(cancellationToken);
        return (true, clean.Count == 0
            ? "Quité el precio por cantidad."
            : "Precio por cantidad guardado: " + string.Join(", ", clean.Select(t => $"desde {t.Units} piezas a {t.Amount.ToString("C0", Mx)} c/u")) + ".");
    }

    /// <summary>Tramos que ya no dejan tu ganancia mínima con el costo de hoy (para avisar).</summary>
    public async Task<List<(MarketplacePublication Publication, QuantityTier Tier, decimal Floor)>> BelowFloorAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking().Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.QuantityPricesJson != null && p.Status == "active")
            .ToListAsync(cancellationToken);
        var result = new List<(MarketplacePublication, QuantityTier, decimal)>();
        foreach (var p in publications)
        {
            var floor = _autoPrice.Floor(p, settings);
            if (floor is null) continue;
            if (Read(p).OrderBy(t => t.Amount).FirstOrDefault(t => t.Amount < floor.Value) is { } low)
                result.Add((p, low, floor.Value));
        }
        return result;
    }

    /// <summary>Lee los tramos de la respuesta de /prices (o del POST): los que tienen min_purchase_unit.</summary>
    private static void ApplyPrices(MarketplacePublication publication, string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array) return;
            var tiers = new List<QuantityTier>();
            foreach (var price in prices.EnumerateArray())
            {
                if (!price.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Object) continue;
                if (!conditions.TryGetProperty("min_purchase_unit", out var min) || !min.TryGetInt32(out var units) || units < 2) continue;
                if (!price.TryGetProperty("amount", out var amount) || !amount.TryGetDecimal(out var value)) continue;
                tiers.Add(new QuantityTier
                {
                    Id = price.TryGetProperty("id", out var id) ? (id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText()) : null,
                    MinUnits = units,
                    Amount = value
                });
            }
            var json = tiers.Count == 0 ? null : JsonSerializer.Serialize(tiers.OrderBy(t => t.MinUnits).ToList());
            publication.QuantityPricesJson = json is { Length: <= 1000 } ? json : null;
            publication.QuantityPricesCheckedAt = DateTime.UtcNow;
        }
        catch (JsonException) { }
    }

    private Task<MarketplacePublication?> LoadAsync(Guid org, int publicationId, CancellationToken cancellationToken) =>
        _context.MarketplacePublications.Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre", cancellationToken);

    private async Task<string?> TokenAsync(Guid org, MarketplacePublication publication, CancellationToken cancellationToken)
    {
        if (publication.MarketplaceAccountId is not { } accountId || publication.ExternalItemId is null) return null;
        try { return await _auth.GetValidAccessTokenAsync(org, accountId); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "No token for quantity prices of account {Account}.", accountId);
            return null;
        }
    }

    private async Task<(bool Ok, HttpStatusCode Status, string Content)> SendAsync(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method == HttpMethod.Get) request.Headers.Add("show-all-prices", "true");
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogInformation("{Method} {Path} returned {Status}.", method, path, (int)response.StatusCode);
            return (response.IsSuccessStatusCode, response.StatusCode, content);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "{Method} {Path} failed.", method, path);
            return (false, HttpStatusCode.ServiceUnavailable, string.Empty);
        }
    }

    private static string? ErrorCode(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()
                : document.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
