using Microsoft.Extensions.Caching.Memory;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Questions;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Metrics;

public sealed record DayPoint(DateOnly Day, decimal Sales, int Orders, int? Visits);

public sealed record KpiValue(decimal Value, decimal? Previous)
{
    /// <summary>Cambio contra el periodo anterior, en %. Null si no hay con qué comparar.</summary>
    public decimal? ChangePercent => Previous is > 0 ? Math.Round((Value - Previous.Value) / Previous.Value * 100m, 1) : null;
}

public sealed record ReputationMetric(string Key, string Label, string Help, decimal? RatePercent, int? Value, decimal GreenLimit, decimal LeaderLimit, string? Period, int? Sales = null)
{
    /// <summary>
    /// Cuántos casos más caben antes de pasar el límite verde, con las ventas del periodo de hoy
    /// (aproximado: Mercado Libre divide los casos entre las ventas del periodo). Null si ya pasó o no hay datos.
    /// </summary>
    public int? RoomLeft => Sales is > 0 && Value is { } value && RatePercent is { } rate && rate <= GreenLimit
        ? Math.Max(0, (int)Math.Floor(GreenLimit / 100m * Sales.Value) - value)
        : null;

    public string Tone => RatePercent is null ? "" : RatePercent <= LeaderLimit ? "ok" : RatePercent <= GreenLimit ? "warn" : "bad";

    /// <summary>"over" si ya pasó el límite verde; "near" si va en 80% o más del límite; null si va bien.</summary>
    public string? Risk => RatePercent is not { } r ? null : r > GreenLimit ? "over" : r >= GreenLimit * 0.8m ? "near" : null;
}

public sealed record ReputationAlert(string Account, string Metric, string Risk, decimal RatePercent, decimal GreenLimit, int? RoomLeft = null);

public sealed record AccountReputation(
    string AccountName,
    int? Level,
    string? LevelColor,
    string? PowerSeller,
    bool Protected,
    int? CompletedSales,
    string? SalesPeriod,
    decimal? PositivePercent,
    decimal? NegativePercent,
    IReadOnlyList<ReputationMetric> Metrics);

public sealed record ProductMetric(
    string ItemId,
    int? PublicationId,
    string Title,
    string? ImageUrl,
    int Units,
    decimal Sales,
    decimal? Profit,
    int? Visits,
    string? Status)
{
    public decimal? ConversionPercent => Visits is > 0 ? Math.Round(Units * 100m / Visits.Value, 2) : null;
}

public sealed record MetricsReport(
    int Days,
    DateOnly From,
    DateOnly To,
    KpiValue Sales,
    KpiValue Orders,
    KpiValue Units,
    KpiValue AverageTicket,
    KpiValue Fees,
    KpiValue? Profit,
    decimal? MarginPercent,
    int UnitsWithCost,
    KpiValue? Visits,
    decimal? ConversionPercent,
    int Cancelled,
    int QuestionsReceived,
    int QuestionsAnswered,
    int QuestionsPending,
    double? MedianAnswerMinutes,
    IReadOnlyList<DayPoint> Daily,
    IReadOnlyList<ProductMetric> TopProducts,
    IReadOnlyList<ProductMetric> VisitsNoSales,
    IReadOnlyList<AccountReputation> Reputation,
    IReadOnlyList<string> Warnings,
    bool HasAccount);

/// <summary>
/// KPIs de la tienda: ventas, ganancia estimada, ticket, comisiones, cancelaciones, preguntas (de la base de la app)
/// y reputación y visitas en vivo de Mercado Libre (/users/{id}, /users/{id}/items_visits/time_window, /items/visits).
/// </summary>
public sealed class MetricsService
{
    /// <summary>Límites de Mercado Libre México: verde y MercadoLíder (developers.mercadolibre, reputación de vendedores).</summary>
    public static readonly (string Key, string Label, string Help, decimal Green, decimal Leader)[] ReputationLimits =
    {
        ("claims", "Reclamos", "Ventas con reclamo del comprador", 1.5m, 1m),
        ("cancellations", "Cancelaciones", "Ventas que cancelaste tú", 1m, 0.5m),
        ("delayed_handling_time", "Despachos con demora", "Paquetes entregados tarde al transporte", 10m, 8m)
    };

    private readonly AppDbContext _context;
    private readonly ICrossBorderPricingService _pricing;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsService> _logger;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public MetricsService(
        AppDbContext context,
        ICrossBorderPricingService pricing,
        IMercadoLibreAuthService auth,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<MetricsService> logger,
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _cache = cache;
        _context = context;
        _pricing = pricing;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));

    private static DateTime Utc(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue).AddHours(7), DateTimeKind.Utc);

    private static DateOnly LocalDay(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(-7));

    public async Task<MetricsReport> GetAsync(Guid organizationId, int days, CancellationToken cancellationToken = default)
    {
        days = days is 7 or 30 or 90 ? days : 30;
        var to = Today;
        var from = to.AddDays(-(days - 1));
        var prevFrom = from.AddDays(-days);
        var warnings = new List<string>();

        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);

        // ── Ventas (base de la app) ──
        var orders = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && o.DateCreated >= Utc(prevFrom) && o.DateCreated < Utc(to.AddDays(1)))
            .Select(o => new
            {
                o.DateCreated,
                o.Status,
                o.TotalAmount,
                o.RealProfitMxn,
                Items = o.Items.Select(i => new { i.ExternalItemId, i.Title, i.Quantity, i.UnitPrice, i.SaleFee })
            })
            .ToListAsync(cancellationToken);

        var paid = orders.Where(o => o.Status == "paid").ToList();
        var current = paid.Where(o => LocalDay(o.DateCreated) >= from).ToList();
        var previous = paid.Where(o => LocalDay(o.DateCreated) < from).ToList();
        var cancelled = orders.Count(o => o.Status == "cancelled" && LocalDay(o.DateCreated) >= from);

        // Costo por publicación para la ganancia estimada.
        var itemIds = paid.SelectMany(o => o.Items.Select(i => i.ExternalItemId)).Distinct().ToList();
        var publications = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == organizationId && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null)
            .Select(p => new
            {
                p.Id,
                ItemId = p.ExternalItemId!,
                p.Status,
                p.IsPublished,
                p.Product.Title,
                p.Product.ImageUrl,
                p.Product.SourcePrice,
                p.Product.SourceCurrency,
                p.Product.SourceMarketplace,
                p.Product.RealLandedCostMxn
            })
            .ToListAsync(cancellationToken);
        var byItem = publications.GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        decimal? LandedFor(string itemId) =>
            byItem.TryGetValue(itemId, out var p)
                ? MarketplaceSync.Services.Automation.ProductCost.Landed(p.RealLandedCostMxn, p.SourcePrice, p.SourceCurrency, p.SourceMarketplace, settings, _pricing)
                : null;
        var landedCache = itemIds.ToDictionary(id => id, LandedFor, StringComparer.OrdinalIgnoreCase);

        (decimal Sales, int Orders, int Units, decimal Fees, decimal Profit, int UnitsWithCost) Sum(IEnumerable<dynamic> list)
        {
            decimal sales = 0, fees = 0, profit = 0;
            int count = 0, units = 0, unitsWithCost = 0;
            foreach (var o in list)
            {
                count++;
                sales += (decimal)o.TotalAmount;
                // Venta ya calculada con el envío real: se usa tal cual.
                var real = (decimal?)o.RealProfitMxn;
                if (real.HasValue)
                    profit += real.Value;
                foreach (var i in o.Items)
                {
                    units += (int)i.Quantity;
                    var fee = (decimal?)i.SaleFee;
                    if (fee.HasValue) fees += fee.Value * (int)i.Quantity;
                    if (real.HasValue)
                    {
                        unitsWithCost += (int)i.Quantity;
                        continue;
                    }
                    var landed = landedCache.GetValueOrDefault((string)i.ExternalItemId);
                    if (landed is { } l && (decimal)i.UnitPrice > 0)
                    {
                        var estimate = _pricing.CalculateProfitFromLandedCost(l, (decimal)i.UnitPrice, settings, fee);
                        profit += estimate.NetProfitMxn * (int)i.Quantity;
                        unitsWithCost += (int)i.Quantity;
                    }
                }
            }
            return (sales, count, units, fees, profit, unitsWithCost);
        }

        var now = Sum(current);
        var before = Sum(previous);

        // ── Preguntas ──
        var questions = await _context.BuyerQuestions.AsNoTracking()
            .Where(q => q.OrganizationId == organizationId && q.DateCreated >= Utc(from))
            .Select(q => new { q.Status, q.DateCreated, q.AnsweredAt })
            .ToListAsync(cancellationToken);
        var pending = await _context.BuyerQuestions.CountAsync(q => q.OrganizationId == organizationId && q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);
        var answerTimes = questions.Where(q => q.AnsweredAt.HasValue && q.AnsweredAt > q.DateCreated)
            .Select(q => (q.AnsweredAt!.Value - q.DateCreated).TotalMinutes).OrderBy(x => x).ToList();
        double? median = answerTimes.Count == 0 ? null : answerTimes[answerTimes.Count / 2];

        // ── Mercado Libre en vivo: reputación y visitas ──
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .Select(a => new { a.Id, a.ExternalAccountId, a.DisplayName })
            .ToListAsync(cancellationToken);

        var reputation = new List<AccountReputation>();
        var dailyVisits = new Dictionary<DateOnly, int>();
        var itemVisits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int? totalVisits = null, previousVisits = null;

        foreach (var account in accounts)
        {
            string token;
            try
            {
                token = await _auth.GetValidAccessTokenAsync(organizationId, account.Id);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "No token for account {Account}.", account.Id);
                warnings.Add($"La cuenta {account.DisplayName} está desconectada: vuelve a conectarla para ver su reputación y visitas.");
                continue;
            }

            try
            {
                using var user = await GetAsync(token, $"/users/{Uri.EscapeDataString(account.ExternalAccountId)}", cancellationToken);
                reputation.Add(ParseReputation(account.DisplayName, user.RootElement));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Reputation failed for {Account}.", account.Id);
                warnings.Add($"Mercado Libre no devolvió la reputación de {account.DisplayName}.");
            }

            try
            {
                // Visitas por día del periodo actual y el anterior (2× días).
                using var window = await GetAsync(token,
                    $"/users/{Uri.EscapeDataString(account.ExternalAccountId)}/items_visits/time_window?last={days * 2}&unit=day&ending={to:yyyy-MM-dd}",
                    cancellationToken);
                if (window.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in results.EnumerateArray())
                    {
                        if (!DateTime.TryParse(Str(r, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d))
                            continue;
                        var day = DateOnly.FromDateTime(d);
                        var total = r.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
                        if (day >= from && day <= to)
                        {
                            dailyVisits[day] = dailyVisits.GetValueOrDefault(day) + total;
                            totalVisits = (totalVisits ?? 0) + total;
                        }
                        else if (day >= prevFrom && day < from)
                        {
                            previousVisits = (previousVisits ?? 0) + total;
                        }
                    }
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Visits time window failed for {Account}.", account.Id);
                warnings.Add($"Mercado Libre no devolvió las visitas de {account.DisplayName}.");
            }

            // Visitas por publicación (activas y pausadas que se ven en la app).
            var accountItems = await _context.MarketplacePublications.AsNoTracking()
                .Where(p => p.MarketplaceAccountId == account.Id && p.IsPublished && p.ExternalItemId != null && p.Status != "closed")
                .Select(p => p.ExternalItemId!)
                .Take(300)
                .ToListAsync(cancellationToken);
            foreach (var chunk in accountItems.Chunk(50))
            {
                try
                {
                    using var visits = await GetAsync(token,
                        $"/items/visits?ids={string.Join(',', chunk.Select(Uri.EscapeDataString))}&date_from={from:yyyy-MM-dd}&date_to={to:yyyy-MM-dd}",
                        cancellationToken);
                    var root = visits.RootElement;
                    var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
                    foreach (var e in entries)
                        if (Str(e, "item_id") is { } id && e.TryGetProperty("total_visits", out var tv) && tv.TryGetInt32(out var count))
                            itemVisits[id] = count;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(exception, "Item visits failed.");
                    break;
                }
            }
        }

        // ── Serie diaria ──
        var daily = new List<DayPoint>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var dayOrders = current.Where(o => LocalDay(o.DateCreated) == d).ToList();
            daily.Add(new DayPoint(d, dayOrders.Sum(o => o.TotalAmount), dayOrders.Count, totalVisits.HasValue ? dailyVisits.GetValueOrDefault(d) : null));
        }

        // ── Productos ──
        var productRows = current.SelectMany(o => o.Items)
            .GroupBy(i => i.ExternalItemId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                byItem.TryGetValue(g.Key, out var pub);
                var landed = landedCache.GetValueOrDefault(g.Key);
                decimal? profit = landed is { } l
                    ? g.Sum(i => _pricing.CalculateProfitFromLandedCost(l, i.UnitPrice, settings, i.SaleFee).NetProfitMxn * i.Quantity)
                    : null;
                return new ProductMetric(g.Key, pub?.Id, pub?.Title ?? g.Select(i => i.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? g.Key,
                    pub?.ImageUrl, g.Sum(i => i.Quantity), g.Sum(i => i.UnitPrice * i.Quantity), profit,
                    itemVisits.TryGetValue(g.Key, out var v) ? v : null, pub?.Status);
            })
            .OrderByDescending(x => x.Sales)
            .Take(10)
            .ToList();

        var soldIds = current.SelectMany(o => o.Items.Select(i => i.ExternalItemId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitsNoSales = itemVisits
            .Where(kv => kv.Value >= 20 && !soldIds.Contains(kv.Key) && byItem.ContainsKey(kv.Key) && byItem[kv.Key].Status == "active")
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .Select(kv =>
            {
                var p = byItem[kv.Key];
                return new ProductMetric(kv.Key, p.Id, p.Title ?? kv.Key, p.ImageUrl, 0, 0, null, kv.Value, p.Status);
            })
            .ToList();

        return new MetricsReport(
            days, from, to,
            new KpiValue(now.Sales, before.Sales),
            new KpiValue(now.Orders, before.Orders),
            new KpiValue(now.Units, before.Units),
            new KpiValue(now.Orders > 0 ? Math.Round(now.Sales / now.Orders, 0) : 0, before.Orders > 0 ? Math.Round(before.Sales / before.Orders, 0) : null),
            new KpiValue(now.Fees, before.Fees),
            now.UnitsWithCost > 0 ? new KpiValue(Math.Round(now.Profit, 0), before.UnitsWithCost > 0 ? Math.Round(before.Profit, 0) : null) : null,
            now.UnitsWithCost > 0 && now.Sales > 0 ? Math.Round(now.Profit / CostCoveredSales(current, landedCache) * 100m, 1) : null,
            now.UnitsWithCost,
            totalVisits.HasValue ? new KpiValue(totalVisits.Value, previousVisits) : null,
            totalVisits is > 0 ? Math.Round(now.Units * 100m / totalVisits.Value, 2) : null,
            cancelled,
            questions.Count,
            questions.Count(q => q.Status == BuyerQuestionStatus.Answered),
            pending,
            median,
            daily,
            productRows,
            visitsNoSales,
            reputation,
            warnings,
            accounts.Count > 0);
    }

    /// <summary>
    /// Métricas de reputación que pasaron o están por pasar el límite verde, de todas las cuentas.
    /// Se guarda 30 minutos en memoria para que Hoy y Telegram no consulten a Mercado Libre en cada vista.
    /// </summary>
    public async Task<IReadOnlyList<ReputationAlert>> GetReputationAlertsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue($"rep-alerts:{organizationId}", out IReadOnlyList<ReputationAlert>? cached) && cached is not null)
            return cached;

        var alerts = new List<ReputationAlert>();
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .Select(a => new { a.Id, a.ExternalAccountId, a.DisplayName })
            .ToListAsync(cancellationToken);
        var ok = true;
        foreach (var account in accounts)
        {
            try
            {
                var token = await _auth.GetValidAccessTokenAsync(organizationId, account.Id);
                using var user = await GetAsync(token, $"/users/{Uri.EscapeDataString(account.ExternalAccountId)}", cancellationToken);
                var rep = ParseReputation(account.DisplayName, user.RootElement);
                alerts.AddRange(rep.Metrics.Where(m => m.Risk is not null)
                    .Select(m => new ReputationAlert(account.DisplayName, m.Label, m.Risk!, m.RatePercent!.Value, m.GreenLimit, m.RoomLeft)));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                ok = false;
                _logger.LogInformation(exception, "Reputation alerts failed for {Account}.", account.Id);
            }
        }

        var result = alerts.OrderBy(a => a.Risk == "over" ? 0 : 1).ToList();
        _cache.Set($"rep-alerts:{organizationId}", (IReadOnlyList<ReputationAlert>)result, ok ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5));
        return result;
    }

    /// <summary>Ventas de las piezas con costo conocido (base del margen).</summary>
    private static decimal CostCoveredSales(IEnumerable<dynamic> orders, Dictionary<string, decimal?> landed)
    {
        decimal total = 0;
        foreach (var o in orders)
            foreach (var i in o.Items)
                if (landed.GetValueOrDefault((string)i.ExternalItemId).HasValue)
                    total += (decimal)i.UnitPrice * (int)i.Quantity;
        return total <= 0 ? 1 : total;
    }

    private static AccountReputation ParseReputation(string accountName, JsonElement user)
    {
        var rep = user.TryGetProperty("seller_reputation", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        int? level = null;
        string? color = null;
        var levelId = rep.ValueKind == JsonValueKind.Object ? Str(rep, "level_id") : null;
        if (levelId is { Length: > 2 } && int.TryParse(levelId[..1], out var lv))
        {
            level = lv;
            color = levelId[2..];
        }

        int? completed = null;
        string? salesPeriod = null;
        decimal? positive = null, negative = null;
        var metrics = new List<ReputationMetric>();
        if (rep.ValueKind == JsonValueKind.Object)
        {
            if (rep.TryGetProperty("transactions", out var tx) && tx.TryGetProperty("ratings", out var ratings))
            {
                positive = Dec(ratings, "positive") is { } p ? Math.Round(p * 100m, 0) : null;
                negative = Dec(ratings, "negative") is { } n ? Math.Round(n * 100m, 0) : null;
            }
            if (rep.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                if (m.TryGetProperty("sales", out var sales))
                {
                    completed = Int(sales, "completed");
                    salesPeriod = Str(sales, "period");
                }
                foreach (var (key, label, help, green, leader) in ReputationLimits)
                {
                    if (!m.TryGetProperty(key, out var metric) || metric.ValueKind != JsonValueKind.Object)
                    {
                        metrics.Add(new ReputationMetric(key, label, help, null, null, green, leader, null));
                        continue;
                    }
                    var rate = Dec(metric, "rate");
                    // Mercado Libre devuelve la tasa como fracción (0.012 = 1.2%).
                    decimal? percent = rate.HasValue ? Math.Round(rate.Value <= 1 ? rate.Value * 100m : rate.Value, 2) : null;
                    metrics.Add(new ReputationMetric(key, label, help, percent, Int(metric, "value"), green, leader, Str(metric, "period"), completed));
                }
            }
        }

        var power = rep.ValueKind == JsonValueKind.Object ? Str(rep, "power_seller_status") : null;
        var isProtected = rep.ValueKind == JsonValueKind.Object && Str(rep, "real_level") is not null;
        return new AccountReputation(accountName, level, color, power, isProtected, completed, salesPeriod, positive, negative, metrics);
    }

    private async Task<JsonDocument> GetAsync(string token, string path, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {content[..Math.Min(content.Length, 300)]}");
        return JsonDocument.Parse(content);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
