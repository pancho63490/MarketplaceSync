using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Métricas de Mercado Ads de un periodo.</summary>
public sealed class AdMetrics
{
    public int Clicks { get; set; }
    public int Prints { get; set; }
    public decimal Cost { get; set; }
    public decimal? Acos { get; set; }
    public int DirectUnits { get; set; }
    public int IndirectUnits { get; set; }
    public decimal DirectAmount { get; set; }
    public decimal TotalAmount { get; set; }
}

public sealed class AdCampaign
{
    public long Id { get; init; }
    public int AccountId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal? Budget { get; init; }
    public decimal? AcosTarget { get; init; }
    public string? Strategy { get; init; }
    public AdMetrics Metrics { get; init; } = new();

    /// <summary>ACOS al que la campaña deja de ganar con tus márgenes (ponderado por lo vendido con anuncios).</summary>
    public decimal? BreakEvenAcos { get; set; }
    public decimal? Profit { get; set; }
}

/// <summary>Un anuncio (publicación) con su gasto y lo que vendió, y cuánto ganas con él.</summary>
public sealed class AdRow
{
    public string ItemId { get; init; } = string.Empty;
    public int AccountId { get; init; }
    public long? CampaignId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public AdMetrics Metrics { get; init; } = new();
    public int? PublicationId { get; init; }
    public decimal? Price { get; init; }

    /// <summary>Ganancia por pieza sin publicidad (precio de hoy, costos de hoy).</summary>
    public decimal? UnitProfit { get; init; }

    /// <summary>ACOS máximo para no perder: ganancia por pieza ÷ precio.</summary>
    public decimal? BreakEvenAcos => UnitProfit is > 0 && Price is > 0 ? Math.Round(UnitProfit.Value / Price.Value * 100m, 1) : UnitProfit is not null ? 0 : null;

    /// <summary>Lo que dejaron las ventas directas del anuncio menos lo que costó.</summary>
    public decimal? Profit => UnitProfit is null ? null : Metrics.DirectUnits * UnitProfit.Value - Metrics.Cost;

    public decimal? RealAcos => Metrics.DirectAmount > 0 ? Math.Round(Metrics.Cost / Metrics.DirectAmount * 100m, 1) : null;

    /// <summary>ok, warn, bad, idle o unknown.</summary>
    public string Verdict =>
        Metrics.Cost <= 0 ? "idle"
        : UnitProfit is null ? "unknown"
        : Metrics.DirectUnits == 0 ? "bad"
        : Profit < 0 ? "bad"
        : BreakEvenAcos is { } max && RealAcos is { } acos && acos > max * 0.8m ? "warn"
        : "ok";

    public bool IsActive => Status == "active";
}

public sealed class AdsReport
{
    public int Days { get; init; }
    public bool Enabled { get; init; }
    public string? Error { get; init; }
    public List<AdCampaign> Campaigns { get; init; } = new();
    public List<AdRow> Ads { get; init; } = new();
    public decimal Cost => Ads.Sum(a => a.Metrics.Cost) is var c && c > 0 ? c : Campaigns.Sum(x => x.Metrics.Cost);
    public decimal Sales => Ads.Sum(a => a.Metrics.TotalAmount) is var s && s > 0 ? s : Campaigns.Sum(x => x.Metrics.TotalAmount);
    public decimal? Acos => Sales > 0 ? Math.Round(Cost / Sales * 100m, 1) : null;
    public decimal Profit => Ads.Where(a => a.Profit.HasValue).Sum(a => a.Profit!.Value);
}

/// <summary>
/// Mercado Ads (Product Ads) con la API oficial: campañas y anuncios con sus métricas, cuánto ganas con cada
/// anuncio contra su ACOS máximo (tu ganancia ÷ precio), pausar o activar anuncios y cambiar presupuesto
/// u objetivo de ACOS de una campaña. Mercado Libre guarda métricas de hasta 90 días.
/// </summary>
public sealed class AdsService
{
    public const string Metrics = "clicks,prints,cost,cpc,acos,direct_units_quantity,indirect_units_quantity,units_quantity,direct_amount,indirect_amount,total_amount";
    private const int PageSize = 50;
    private const int MaxPages = 10;

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ICrossBorderPricingService _pricing;
    private readonly PublicationEconomics _economics;
    private readonly ILogger<AdsService> _logger;

    public AdsService(
        AppDbContext context,
        IMercadoLibreAuthService auth,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IMemoryCache cache,
        ICrossBorderPricingService pricing,
        PublicationEconomics economics,
        ILogger<AdsService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _cache = cache;
        _pricing = pricing;
        _economics = economics;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static DateOnly TodayJuarez => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));

    // ─────────────────────────────────────────────────────────────
    // Lectura
    // ─────────────────────────────────────────────────────────────

    /// <summary>Campañas y anuncios de los últimos <paramref name="days"/> días (hasta 90) con su ganancia.</summary>
    public async Task<AdsReport> ReportAsync(Guid org, int days, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 90);
        var to = TodayJuarez;
        var from = to.AddDays(-(days - 1));
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0)
            return new AdsReport { Days = days, Enabled = false, Error = "Conecta tu cuenta de Mercado Libre." };

        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null)
            .ToListAsync(cancellationToken);
        var byItem = publications.GroupBy(p => p.ExternalItemId!).ToDictionary(g => g.Key, g => g.First());

        var campaigns = new List<AdCampaign>();
        var ads = new List<AdRow>();
        var enabled = false;
        string? error = null;
        foreach (var account in accounts)
        {
            var token = await TokenAsync(org, account.Id);
            if (token is null) continue;
            var advertiser = await AdvertiserAsync(account.Id, account.ExternalAccountId, token, cancellationToken);
            if (advertiser is null) continue;
            enabled = true;
            var (advertiserId, site) = advertiser.Value;

            var (campaignsOk, campaignJson) = await GetPagedAsync(token,
                $"/marketplace/advertising/{site}/advertisers/{advertiserId}/product_ads/campaigns",
                $"/advertising/advertisers/{advertiserId}/product_ads/campaigns", from, to, cancellationToken);
            if (!campaignsOk) error ??= "Mercado Libre no regresó las campañas.";
            foreach (var c in campaignJson)
                campaigns.Add(new AdCampaign
                {
                    Id = Long(c, "id") ?? 0,
                    AccountId = account.Id,
                    Name = Str(c, "name") ?? "Campaña",
                    Status = Str(c, "status") ?? string.Empty,
                    Budget = Dec(c, "budget") ?? Dec(c, "daily_budget"),
                    AcosTarget = Dec(c, "acos_target"),
                    Strategy = Str(c, "strategy")?.ToLowerInvariant(),
                    Metrics = ParseMetrics(c)
                });

            var (adsOk, adJson) = await GetPagedAsync(token,
                $"/marketplace/advertising/{site}/advertisers/{advertiserId}/product_ads/ads",
                $"/advertising/advertisers/{advertiserId}/product_ads/items", from, to, cancellationToken);
            if (!adsOk) error ??= "Mercado Libre no regresó los anuncios.";
            foreach (var a in adJson)
            {
                var itemId = Str(a, "item_id") ?? Str(a, "id");
                if (itemId is null) continue;
                byItem.TryGetValue(itemId, out var publication);
                var money = publication is null ? null : _economics.Calculate(publication, publication.Product, settings);
                ads.Add(new AdRow
                {
                    ItemId = itemId,
                    AccountId = account.Id,
                    CampaignId = Long(a, "campaign_id") is > 0 and var cid ? cid : null,
                    Title = publication?.Product.Title ?? Str(a, "title") ?? itemId,
                    Status = Str(a, "status") ?? string.Empty,
                    Metrics = ParseMetrics(a),
                    PublicationId = publication?.Id,
                    Price = publication?.Price ?? Dec(a, "price"),
                    UnitProfit = money?.Profit.NetProfitMxn
                });
            }
        }

        foreach (var campaign in campaigns)
        {
            var mine = ads.Where(a => a.CampaignId == campaign.Id && a.AccountId == campaign.AccountId && a.UnitProfit.HasValue).ToList();
            var amount = mine.Sum(a => a.Metrics.DirectAmount);
            var margin = mine.Sum(a => a.Metrics.DirectUnits * Math.Max(0, a.UnitProfit!.Value));
            campaign.BreakEvenAcos = amount > 0 ? Math.Round(margin / amount * 100m, 1) : null;
            campaign.Profit = mine.Count > 0 ? mine.Sum(a => a.Profit ?? 0) : null;
        }

        return new AdsReport
        {
            Days = days,
            Enabled = enabled,
            Error = enabled ? error : "Tu cuenta no tiene Mercado Ads activado (o la app no tiene permiso de publicidad).",
            Campaigns = campaigns.OrderByDescending(c => c.Status == "active").ThenByDescending(c => c.Metrics.Cost).ToList(),
            Ads = ads.OrderBy(a => a.Verdict switch { "bad" => 0, "warn" => 1, "unknown" => 2, "ok" => 3, _ => 4 })
                     .ThenByDescending(a => a.Metrics.Cost).ToList()
        };
    }

    /// <summary>Gasto en Mercado Ads entre dos fechas (para Gastos y ganancia); null si no hay Ads o pasa de 90 días.</summary>
    public async Task<decimal?> SpendAsync(Guid org, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var today = TodayJuarez;
        if (to > today) to = today;
        if (from < today.AddDays(-89) || to < from)
            return null;
        var key = $"ads-spend:{org}:{from:yyyyMMdd}:{to:yyyyMMdd}";
        if (_cache.TryGetValue(key, out decimal? cached))
            return cached;

        decimal? total = null;
        try
        {
            var accounts = await _context.MarketplaceAccounts.AsNoTracking()
                .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
                .ToListAsync(cancellationToken);
            foreach (var account in accounts)
            {
                var token = await TokenAsync(org, account.Id);
                if (token is null) continue;
                var advertiser = await AdvertiserAsync(account.Id, account.ExternalAccountId, token, cancellationToken);
                if (advertiser is null) continue;
                var (advertiserId, site) = advertiser.Value;
                var (ok, campaigns) = await GetPagedAsync(token,
                    $"/marketplace/advertising/{site}/advertisers/{advertiserId}/product_ads/campaigns",
                    $"/advertising/advertisers/{advertiserId}/product_ads/campaigns", from, to, cancellationToken);
                if (!ok) continue;
                total = (total ?? 0) + campaigns.Sum(c => ParseMetrics(c).Cost);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Ads spend for {Org} failed.", org);
            return null;
        }
        _cache.Set(key, total, TimeSpan.FromHours(1));
        return total;
    }

    // ─────────────────────────────────────────────────────────────
    // Cambios
    // ─────────────────────────────────────────────────────────────

    /// <summary>Pausa o activa el anuncio de una publicación.</summary>
    public async Task<(bool Ok, string Message)> SetAdStatusAsync(Guid org, int accountId, string itemId, string status, CancellationToken cancellationToken = default)
    {
        if (status is not ("active" or "paused"))
            return (false, "Estado no válido.");
        var (token, advertiser, error) = await ContextAsync(org, accountId, cancellationToken);
        if (token is null || advertiser is null)
            return (false, error!);
        var body = new JsonObject { ["status"] = status };
        var (ok, content) = await SendAsync(token, HttpMethod.Put,
            $"/marketplace/advertising/{advertiser.Value.Site}/product_ads/ads/{Uri.EscapeDataString(itemId)}?channel=marketplace", body, "2", cancellationToken);
        if (!ok)
            return (false, "Mercado Libre no cambió el anuncio: " + MercadoLibreListingClient.DescribeError(content));
        var title = await TitleAsync(org, itemId, cancellationToken);
        return (true, (status == "paused" ? "Pausé el anuncio de " : "Activé el anuncio de ") + $"«{title}».");
    }

    /// <summary>Cambia estado, presupuesto diario u objetivo de ACOS de una campaña.</summary>
    public async Task<(bool Ok, string Message)> UpdateCampaignAsync(Guid org, int accountId, long campaignId, string? status, decimal? budget, decimal? acosTarget, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        if (status is "active" or "paused") body["status"] = status;
        if (budget is > 0) body["budget"] = Math.Round(budget.Value, 2);
        if (acosTarget is not null)
        {
            if (acosTarget <= 3 || acosTarget >= 500)
                return (false, "El ACOS objetivo debe ser mayor a 3% (Mercado Libre no acepta menos).");
            body["acos_target"] = Math.Round(acosTarget.Value, 1);
        }
        if (body.Count == 0)
            return (false, "No hay nada que cambiar.");
        var (token, advertiser, error) = await ContextAsync(org, accountId, cancellationToken);
        if (token is null || advertiser is null)
            return (false, error!);
        var (ok, content) = await SendAsync(token, HttpMethod.Put,
            $"/marketplace/advertising/{advertiser.Value.Site}/product_ads/campaigns/{campaignId}", body, "2", cancellationToken);
        if (!ok)
            return (false, "Mercado Libre no cambió la campaña: " + MercadoLibreListingClient.DescribeError(content));
        var parts = new List<string>();
        if (body.ContainsKey("status")) parts.Add(status == "paused" ? "pausada" : "activa");
        if (body.ContainsKey("budget")) parts.Add($"presupuesto {budget!.Value.ToString("C0", CultureInfo.GetCultureInfo("es-MX"))} al día");
        if (body.ContainsKey("acos_target")) parts.Add($"ACOS objetivo {acosTarget:0.#}%");
        return (true, "Campaña actualizada: " + string.Join(", ", parts) + ".");
    }

    // ─────────────────────────────────────────────────────────────
    // Infraestructura
    // ─────────────────────────────────────────────────────────────

    private async Task<(string? Token, (long Id, string Site)? Advertiser, string? Error)> ContextAsync(Guid org, int accountId, CancellationToken cancellationToken)
    {
        var account = await _context.MarketplaceAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.OrganizationId == org, cancellationToken);
        if (account is null)
            return (null, null, "No encontré la cuenta.");
        var token = await TokenAsync(org, accountId);
        if (token is null)
            return (null, null, "No hay sesión de Mercado Libre para esa cuenta.");
        var advertiser = await AdvertiserAsync(accountId, account.ExternalAccountId, token, cancellationToken);
        return advertiser is null ? (token, null, "Esa cuenta no tiene Mercado Ads activado.") : (token, advertiser, null);
    }

    private async Task<(long Id, string Site)?> AdvertiserAsync(int accountId, string userId, string token, CancellationToken cancellationToken)
    {
        var key = "ads-advertiser:" + accountId;
        if (_cache.TryGetValue(key, out (long, string)? cached))
            return cached;
        var (ok, content) = await SendAsync(token, HttpMethod.Get,
            $"/advertising/advertisers?product_id=PADS&user_id={Uri.EscapeDataString(userId)}", null, "1", cancellationToken);
        (long, string)? result = null;
        if (ok)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("advertisers", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var a in list.EnumerateArray())
                        if (Long(a, "advertiser_id") is { } id)
                        {
                            result = (id, Str(a, "site_id") ?? "MLM");
                            break;
                        }
            }
            catch (JsonException) { }
        }
        _cache.Set(key, result, TimeSpan.FromHours(result is null ? 1 : 12));
        return result;
    }

    /// <summary>Lee todas las páginas (ruta nueva y, si falla, la anterior) con métricas del periodo.</summary>
    private async Task<(bool Ok, List<JsonElement> Results)> GetPagedAsync(string token, string path, string fallbackPath, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        foreach (var basePath in new[] { path, fallbackPath })
        {
            var results = new List<JsonElement>();
            var ok = true;
            for (var page = 0; page < MaxPages; page++)
            {
                var url = $"{basePath}?limit={PageSize}&offset={page * PageSize}&date_from={from:yyyy-MM-dd}&date_to={to:yyyy-MM-dd}&metrics={Metrics}";
                var (success, content) = await SendAsync(token, HttpMethod.Get, url, null, "2", cancellationToken);
                if (!success) { ok = false; break; }
                int total;
                try
                {
                    using var document = JsonDocument.Parse(content);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("results", out var list) || list.ValueKind != JsonValueKind.Array) break;
                    results.AddRange(list.EnumerateArray().Select(e => e.Clone()));
                    total = root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : results.Count;
                    if (list.GetArrayLength() < PageSize || results.Count >= total) break;
                }
                catch (JsonException) { ok = false; break; }
            }
            if (ok || results.Count > 0)
                return (true, results);
        }
        return (false, new List<JsonElement>());
    }

    private async Task<string> TitleAsync(Guid org, string itemId, CancellationToken cancellationToken) =>
        await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.ExternalItemId == itemId)
            .Select(p => p.Product.Title)
            .FirstOrDefaultAsync(cancellationToken) ?? itemId;

    private async Task<string?> TokenAsync(Guid org, int accountId)
    {
        try { return await _auth.GetValidAccessTokenAsync(org, accountId); }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "No token for ads (account {AccountId}).", accountId);
            return null;
        }
    }

    private async Task<(bool Ok, string Content)> SendAsync(string token, HttpMethod method, string path, JsonNode? body, string apiVersion, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("api-version", apiVersion);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
                _logger.LogWarning("Ads {Method} {Path} returned {Status}: {Body}", method, path, (int)response.StatusCode, content.Length > 300 ? content[..300] : content);
            return (response.IsSuccessStatusCode, content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Ads {Method} {Path} failed.", method, path);
            return (false, string.Empty);
        }
    }

    private static AdMetrics ParseMetrics(JsonElement e)
    {
        var m = e.TryGetProperty("metrics", out var x) && x.ValueKind == JsonValueKind.Object ? x
            : e.TryGetProperty("metrics_summary", out var y) && y.ValueKind == JsonValueKind.Object ? y : default;
        if (m.ValueKind != JsonValueKind.Object)
            return new AdMetrics();
        var direct = (int)(Dec(m, "direct_units_quantity") ?? 0);
        var units = (int)(Dec(m, "units_quantity") ?? direct);
        return new AdMetrics
        {
            Clicks = (int)(Dec(m, "clicks") ?? 0),
            Prints = (int)(Dec(m, "prints") ?? 0),
            Cost = Dec(m, "cost") ?? 0,
            Acos = Dec(m, "acos"),
            DirectUnits = direct,
            IndirectUnits = (int)(Dec(m, "indirect_units_quantity") ?? Math.Max(0, units - direct)),
            DirectAmount = Dec(m, "direct_amount") ?? 0,
            TotalAmount = Dec(m, "total_amount") ?? Dec(m, "direct_amount") ?? 0
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
              : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s : null
            : null;
}
