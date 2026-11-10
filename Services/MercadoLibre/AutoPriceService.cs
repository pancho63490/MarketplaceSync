using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.CrossBorder;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed record AutoPriceRuleOption(string RuleId, string Title, string? Description);

public sealed record AutoPriceHistoryEntry(DateTime When, decimal Price, decimal? PercentChange, string? Event);

/// <summary>Lo que muestra el panel de una publicación.</summary>
public sealed class AutoPricePanel
{
    public required MarketplacePublication Publication { get; init; }
    public List<AutoPriceRuleOption> Rules { get; init; } = new();
    /// <summary>Precio más bajo que deja tu ganancia mínima (null si falta el costo).</summary>
    public decimal? Floor { get; init; }
    public decimal SuggestedMax { get; init; }
    public List<AutoPriceHistoryEntry> History { get; init; } = new();
    /// <summary>Por qué no se puede usar (cuenta sin permiso, publicación no compatible…).</summary>
    public string? Unavailable { get; init; }
}

/// <summary>Un cambio que hizo la revisión diaria (para avisar por Telegram).</summary>
public sealed record AutoPriceEvent(MarketplacePublication Publication, string Kind, decimal? OldMin, decimal? NewMin);

/// <summary>
/// Precio automático de Mercado Libre (<c>/pricing-automation/items/{id}/…</c>): Mercado Libre sube y baja el precio
/// para competir, entre un mínimo y un máximo. La app pone el mínimo en el precio que deja tu ganancia mínima
/// (con tu costo real del viaje o el estimado) y lo vuelve a calcular cada día si cambia el dólar o el costo.
/// </summary>
public sealed class AutoPriceService
{
    public const string RuleInternal = "INT";
    public const string RuleInternalExternal = "INT_EXT";
    private const decimal MinChangeToUpdate = 0.02m;
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly PublicationEconomics _economics;
    private readonly ICrossBorderPricingService _pricing;
    private readonly ILogger<AutoPriceService> _logger;

    public AutoPriceService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory, IConfiguration configuration,
        PublicationEconomics economics, ICrossBorderPricingService pricing, ILogger<AutoPriceService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _economics = economics;
        _pricing = pricing;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static string RuleLabel(string? rule) => rule switch
    {
        RuleInternal => "Igualar el precio más bajo en Mercado Libre",
        RuleInternalExternal => "Igualar el más bajo dentro y fuera de Mercado Libre",
        null => "—",
        _ => rule
    };

    public static string StatusLabel(string? status, string? cause) => status switch
    {
        "ACTIVE" => "Activo",
        "PAUSED" => "En pausa" + (CauseLabel(cause) is { } c ? ": " + c : ""),
        _ => "Apagado"
    };

    public static string? CauseLabel(string? cause) => cause switch
    {
        "ITEM_NO_ACTIVE" => "la publicación no está activa",
        "PROMO" => "está en una promoción",
        "COMPETITORS" => "no hay con quién compararla",
        null or "" => null,
        _ => cause.ToLowerInvariant().Replace('_', ' ')
    };

    /// <summary>Errores de Mercado Libre en palabras del vendedor.</summary>
    public static string ErrorLabel(string? code, HttpStatusCode status) => code switch
    {
        "item_not_automatizable" or "item_not_automatable" => "Mercado Libre no ofrece precio automático para esta publicación.",
        "user_not_authorized" => "Tu cuenta todavía no tiene el precio automático de Mercado Libre.",
        "automation_operation_not_allowed" => "Mercado Libre no deja cambiarlo ahora (puede estar en una promoción).",
        "max_price_greater_than_category" => "El precio máximo es más alto de lo que permite la categoría; bájalo.",
        "automation_already_created" => "La publicación ya tenía precio automático.",
        "automation_not_found" => "La publicación no tiene precio automático.",
        "argument_not_valid" or "bad_request" => "Mercado Libre rechazó los precios: revisa el mínimo y el máximo.",
        "item_not_found" => "Mercado Libre no encontró la publicación.",
        _ when status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Mercado Libre no dio permiso (vuelve a conectar la cuenta).",
        _ => "Mercado Libre no respondió. Intenta de nuevo en un rato."
    };

    /// <summary>El precio más bajo que todavía deja tu ganancia mínima (redondeado hacia arriba). Null si falta el costo.</summary>
    public decimal? Floor(MarketplacePublication publication, CrossBorderSettings settings)
    {
        var money = _economics.Calculate(publication, publication.Product, settings);
        if (money is null) return null;
        var floor = Math.Ceiling(money.SuggestedPriceMxn);
        // Confirma con el cálculo completo (cargo fijo o envío según el precio) y sube si hace falta.
        for (var i = 0; i < 8; i++)
        {
            var at = _economics.Calculate(publication, publication.Product, settings, floor);
            if (at is null || at.Profit.IsProfitable) break;
            floor = Math.Ceiling(floor * 1.02m);
        }
        return floor;
    }

    public static decimal DefaultMax(decimal? price, decimal floor) =>
        Math.Ceiling(Math.Max(price ?? 0, floor) * 1.3m / 10m) * 10m;

    // ─────────────────────────────────────────────────────────────
    // Panel de una publicación
    // ─────────────────────────────────────────────────────────────

    public async Task<AutoPricePanel?> PanelAsync(Guid org, int publicationId, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication is null) return null;
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var floor = Floor(publication, settings);
        var token = await TokenAsync(org, publication, cancellationToken);
        if (token is null || publication.ExternalItemId is null)
            return new AutoPricePanel { Publication = publication, Floor = floor, SuggestedMax = DefaultMax(publication.Price, floor ?? 0), Unavailable = "No hay sesión de Mercado Libre para esta publicación." };

        var itemId = Uri.EscapeDataString(publication.ExternalItemId);
        var rules = new List<AutoPriceRuleOption>();
        string? unavailable = null;
        var (rulesOk, rulesStatus, rulesBody) = await SendAsync(HttpMethod.Get, $"/pricing-automation/items/{itemId}/rules", token, null, cancellationToken);
        if (rulesOk)
        {
            using var document = JsonDocument.Parse(rulesBody);
            if (document.RootElement.TryGetProperty("rules", out var list) && list.ValueKind == JsonValueKind.Array)
                rules = list.EnumerateArray()
                    .Select(r => Str(r, "rule_id"))
                    .Where(id => id is not null)
                    .Select(id => new AutoPriceRuleOption(id!, RuleLabel(id), null))
                    .ToList();
            if (rules.Count == 0) unavailable = "Mercado Libre no ofrece precio automático para esta publicación.";
        }
        else
        {
            unavailable = ErrorLabel(ErrorCode(rulesBody), rulesStatus);
        }

        await ReadAutomationAsync(publication, itemId, token, cancellationToken);

        var history = new List<AutoPriceHistoryEntry>();
        if (publication.AutoPriceStatus is not null)
        {
            var (historyOk, _, historyBody) = await SendAsync(HttpMethod.Get, $"/pricing-automation/items/{itemId}/price/history?days=30&size=8", token, null, cancellationToken);
            if (historyOk) history = ParseHistory(historyBody);
        }
        await _context.SaveChangesAsync(cancellationToken);

        return new AutoPricePanel
        {
            Publication = publication,
            Rules = rules,
            Floor = floor,
            SuggestedMax = DefaultMax(publication.Price, floor ?? 0),
            History = history,
            Unavailable = publication.AutoPriceStatus is null ? unavailable : null
        };
    }

    /// <summary>Prende o actualiza el precio automático. El mínimo nunca queda abajo de tu ganancia mínima.</summary>
    public async Task<(bool Ok, string Message)> EnableAsync(Guid org, int publicationId, string? rule, decimal? min, decimal? max, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication?.ExternalItemId is null) return (false, "No encontré esa publicación.");
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var floor = Floor(publication, settings);
        if (floor is null)
            return (false, "Primero pon el costo del producto: sin costo no sé cuál es tu precio mínimo.");
        var minPrice = Math.Ceiling(min is > 0 ? min.Value : floor.Value);
        if (minPrice < floor.Value)
            return (false, $"Con {minPrice.ToString("C0", Mx)} no ganas lo mínimo; el mínimo puede ser {floor.Value.ToString("C0", Mx)} o más.");
        var maxPrice = Math.Ceiling(max is > 0 ? max.Value : DefaultMax(publication.Price, minPrice));
        if (maxPrice <= minPrice)
            return (false, "El precio máximo debe ser más alto que el mínimo.");
        var ruleId = rule is RuleInternal or RuleInternalExternal ? rule : RuleInternal;

        var token = await TokenAsync(org, publication, cancellationToken);
        if (token is null) return (false, "No hay sesión de Mercado Libre para esta publicación.");
        var itemId = Uri.EscapeDataString(publication.ExternalItemId);
        var body = new { rule_id = ruleId, min_price = minPrice, max_price = maxPrice };

        // Si ya tenía, se actualiza; si no, se crea.
        var hadAutomation = publication.AutoPriceStatus is not null;
        var (ok, status, content) = await SendAsync(hadAutomation ? HttpMethod.Put : HttpMethod.Post, $"/pricing-automation/items/{itemId}/automation", token, body, cancellationToken);
        if (!ok && ErrorCode(content) is "automation_already_created")
            (ok, status, content) = await SendAsync(HttpMethod.Put, $"/pricing-automation/items/{itemId}/automation", token, body, cancellationToken);
        else if (!ok && hadAutomation && ErrorCode(content) is "automation_not_found")
            (ok, status, content) = await SendAsync(HttpMethod.Post, $"/pricing-automation/items/{itemId}/automation", token, body, cancellationToken);
        if (!ok)
            return (false, ErrorLabel(ErrorCode(content), status));

        Apply(publication, content);
        publication.AutoPriceRule ??= ruleId;
        publication.AutoPriceMin ??= minPrice;
        publication.AutoPriceMax ??= maxPrice;
        publication.AutoPriceStatus ??= "ACTIVE";
        await _context.SaveChangesAsync(cancellationToken);
        return (true, $"Precio automático {(hadAutomation ? "actualizado" : "activado")}: Mercado Libre moverá el precio entre {minPrice.ToString("C0", Mx)} y {maxPrice.ToString("C0", Mx)}.");
    }

    public async Task<(bool Ok, string Message)> DisableAsync(Guid org, int publicationId, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication?.ExternalItemId is null) return (false, "No encontré esa publicación.");
        var token = await TokenAsync(org, publication, cancellationToken);
        if (token is null) return (false, "No hay sesión de Mercado Libre para esta publicación.");
        var (ok, status, content) = await SendAsync(HttpMethod.Delete, $"/pricing-automation/items/{Uri.EscapeDataString(publication.ExternalItemId)}/automation", token, null, cancellationToken);
        if (!ok && ErrorCode(content) != "automation_not_found")
            return (false, ErrorLabel(ErrorCode(content), status));
        Clear(publication);
        await _context.SaveChangesAsync(cancellationToken);
        return (true, "Precio automático apagado: el precio se queda donde está y lo manejas tú.");
    }

    /// <summary>Prende el precio automático en varias publicaciones con el mínimo de tu ganancia y máximo +30%.</summary>
    public async Task<(int Done, List<string> Problems)> EnableManyAsync(Guid org, IEnumerable<int> publicationIds, string? rule, CancellationToken cancellationToken = default)
    {
        var done = 0;
        var problems = new List<string>();
        foreach (var id in publicationIds.Distinct().Take(100))
        {
            var (ok, message) = await EnableAsync(org, id, rule, null, null, cancellationToken);
            if (ok) { done++; continue; }
            var title = await _context.MarketplacePublications.AsNoTracking().Where(p => p.Id == id).Select(p => p.Product.Title).FirstOrDefaultAsync(cancellationToken);
            problems.Add($"«{Short(title, 40)}»: {message}");
        }
        return (done, problems);
    }

    // ─────────────────────────────────────────────────────────────
    // Revisión diaria
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Revisa las publicaciones con precio automático: estado en Mercado Libre y el mínimo contra tu costo de hoy.
    /// Si el mínimo ya no deja tu ganancia mínima (subió el dólar o el costo), lo sube; si bajó más de 2%, lo baja.
    /// </summary>
    public async Task<List<AutoPriceEvent>> SyncAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var events = new List<AutoPriceEvent>();
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var publications = await _context.MarketplacePublications.Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.AutoPriceStatus != null && p.ExternalItemId != null && p.MarketplaceAccountId != null)
            .Take(300)
            .ToListAsync(cancellationToken);
        var tokens = new Dictionary<int, string?>();
        foreach (var publication in publications)
        {
            var accountId = publication.MarketplaceAccountId!.Value;
            if (!tokens.TryGetValue(accountId, out var token))
                tokens[accountId] = token = await TokenAsync(org, publication, cancellationToken);
            if (token is null) continue;

            var itemId = Uri.EscapeDataString(publication.ExternalItemId!);
            var wasPaused = publication.AutoPriceStatus == "PAUSED";
            var found = await ReadAutomationAsync(publication, itemId, token, cancellationToken);
            if (!found)
            {
                events.Add(new AutoPriceEvent(publication, "removed", null, null));
                continue;
            }
            if (!wasPaused && publication.AutoPriceStatus == "PAUSED")
                events.Add(new AutoPriceEvent(publication, "paused", null, null));

            var floor = Floor(publication, settings);
            if (floor is null || publication.AutoPriceMin is not { } currentMin) continue;
            var raise = floor.Value > currentMin;
            var lower = floor.Value < currentMin * (1 - MinChangeToUpdate);
            if (!raise && !lower) continue;

            var newMax = Math.Max(publication.AutoPriceMax ?? 0, Math.Ceiling(floor.Value * 1.15m));
            var body = new { rule_id = publication.AutoPriceRule ?? RuleInternal, min_price = floor.Value, max_price = newMax };
            var (ok, _, content) = await SendAsync(HttpMethod.Put, $"/pricing-automation/items/{itemId}/automation", token, body, cancellationToken);
            if (!ok)
            {
                _logger.LogInformation("Could not update automation minimum of {Item}: {Body}", publication.ExternalItemId, content.Length > 200 ? content[..200] : content);
                continue;
            }
            Apply(publication, content);
            publication.AutoPriceMin = floor.Value;
            publication.AutoPriceMax = Math.Max(publication.AutoPriceMax ?? 0, newMax);
            events.Add(new AutoPriceEvent(publication, raise ? "min_raised" : "min_lowered", currentMin, floor.Value));
        }
        await _context.SaveChangesAsync(cancellationToken);
        return events;
    }

    // ─────────────────────────────────────────────────────────────

    /// <summary>Lee la automatización en Mercado Libre. False si ya no existe (Mercado Libre la quitó).</summary>
    private async Task<bool> ReadAutomationAsync(MarketplacePublication publication, string itemId, string token, CancellationToken cancellationToken)
    {
        var (ok, status, content) = await SendAsync(HttpMethod.Get, $"/pricing-automation/items/{itemId}/automation", token, null, cancellationToken);
        publication.AutoPriceCheckedAt = DateTime.UtcNow;
        if (ok)
        {
            Apply(publication, content);
            return true;
        }
        if (status == HttpStatusCode.NotFound && ErrorCode(content) is "automation_not_found" or null)
        {
            Clear(publication);
            return false;
        }
        return true; // Error temporal: se deja como estaba.
    }

    private static void Apply(MarketplacePublication publication, string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (Str(root, "status") is { Length: > 0 and <= 20 } status) publication.AutoPriceStatus = status.ToUpperInvariant();
            if (root.TryGetProperty("item_rule", out var rule) && Str(rule, "rule_id") is { Length: <= 20 } ruleId) publication.AutoPriceRule = ruleId;
            if (Dec(root, "min_price") is { } min) publication.AutoPriceMin = min;
            if (Dec(root, "max_price") is { } max) publication.AutoPriceMax = max;
            publication.AutoPriceCause = root.TryGetProperty("status_detail", out var detail) && Str(detail, "cause") is { Length: <= 40 } cause ? cause : null;
        }
        catch (JsonException) { }
    }

    private static void Clear(MarketplacePublication publication)
    {
        publication.AutoPriceStatus = null;
        publication.AutoPriceCause = null;
        publication.AutoPriceCheckedAt = DateTime.UtcNow;
    }

    private static List<AutoPriceHistoryEntry> ParseHistory(string content)
    {
        var list = new List<AutoPriceHistoryEntry>();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var result = root.TryGetProperty("result", out var r) ? r : root;
            if (!result.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in items.EnumerateArray())
            {
                if (Dec(item, "price") is not { } price) continue;
                var when = DateTimeOffset.TryParse(Str(item, "date_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : DateTime.UtcNow;
                list.Add(new AutoPriceHistoryEntry(when, price, Dec(item, "percent_change"), Str(item, "event")));
            }
        }
        catch (JsonException) { }
        return list.OrderByDescending(h => h.When).Take(8).ToList();
    }

    private Task<MarketplacePublication?> LoadAsync(Guid org, int publicationId, CancellationToken cancellationToken) =>
        _context.MarketplacePublications.Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre", cancellationToken);

    private async Task<string?> TokenAsync(Guid org, MarketplacePublication publication, CancellationToken cancellationToken)
    {
        if (publication.MarketplaceAccountId is not { } accountId) return null;
        try { return await _auth.GetValidAccessTokenAsync(org, accountId); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "No token for price automation of account {Account}.", accountId);
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
            return Str(document.RootElement, "error") ?? Str(document.RootElement, "code");
        }
        catch (JsonException) { return null; }
    }

    private static string Short(string? text, int max) => string.IsNullOrEmpty(text) ? "publicación" : text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
