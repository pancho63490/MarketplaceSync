using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Orders;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Finance;

public sealed class MoneySummary
{
    /// <summary>Lo que Mercado Pago aún no libera (neto de las ventas).</summary>
    public decimal Pending { get; init; }
    public decimal ReleasedThisMonth { get; init; }
    public List<(DateOnly Day, decimal Amount, int Sales)> Upcoming { get; init; } = new();
    public List<SalesOrder> Differences { get; init; } = new();
    public List<SalesOrder> Recent { get; init; } = new();
    public int NotChecked { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Mercado Pago: lee el pago de cada venta (<c>/v1/payments/{id}</c>, con el mismo acceso de la cuenta de
/// Mercado Libre) para saber cuánto te deja de verdad y cuándo se libera el dinero, y lo compara con lo que la
/// app esperaba (cobrado − comisión − envío − retención). Diferencias de más de $10 (o 2%) se avisan.
/// </summary>
public sealed class MercadoPagoService
{
    public const decimal ToleranceMxn = 10m;
    private static readonly TimeSpan PendingRecheck = TimeSpan.FromHours(6);

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MercadoPagoService> _logger;

    public MercadoPagoService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<MercadoPagoService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string MlBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";
    private string MpBase => _configuration["MercadoPago:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadopago.com";

    public static bool HasDifference(SalesOrder o) =>
        o.MpNetReceived is { } net && o.MpExpectedNet is { } expected &&
        Math.Abs(net - expected) > Math.Max(ToleranceMxn, Math.Abs(expected) * 0.02m);

    /// <summary>Lo que la app esperaba recibir de la venta (null si aún no calcula comisiones y envío).</summary>
    public static decimal? Expected(SalesOrder order)
    {
        if (order.ProfitComputedAt is null) return null;
        var charged = order.Items.Count > 0 ? order.Items.Sum(i => i.UnitPrice * i.Quantity) : order.TotalAmount;
        var fees = order.Items.Sum(i => (i.SaleFee ?? 0) * i.Quantity);
        return Math.Round(charged - fees - (order.SellerShippingCostMxn ?? 0) - (order.TaxWithheldMxn ?? 0), 2);
    }

    /// <summary>Revisa ventas pagadas de los últimos 60 días sin revisar o con dinero por liberar. Regresa las que tienen diferencia nueva.</summary>
    public async Task<List<SalesOrder>> ReconcileAsync(Guid org, int limit = 40, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-60);
        var recheck = now - PendingRecheck;
        var orders = await _context.SalesOrders.Include(o => o.Items)
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= since &&
                        (o.MpCheckedAt == null || (o.MpReleaseStatus != "released" && o.MpCheckedAt < recheck)))
            .OrderBy(o => o.MpCheckedAt ?? DateTime.MinValue).ThenByDescending(o => o.DateCreated)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var tokens = new Dictionary<int, string?>();
        var newDifferences = new List<SalesOrder>();
        foreach (var order in orders)
        {
            if (!tokens.TryGetValue(order.MarketplaceAccountId, out var token))
            {
                try { token = await _auth.GetValidAccessTokenAsync(org, order.MarketplaceAccountId); }
                catch (Exception exception) { _logger.LogInformation(exception, "No token for payments of account {Account}.", order.MarketplaceAccountId); token = null; }
                tokens[order.MarketplaceAccountId] = token;
            }
            if (token is null) { order.MpError = "No hay sesión de Mercado Libre."; continue; }

            var hadDifference = HasDifference(order);
            await CheckOrderAsync(order, token, cancellationToken);
            if (!hadDifference && HasDifference(order))
                newDifferences.Add(order);
        }
        await _context.SaveChangesAsync(cancellationToken);
        return newDifferences;
    }

    private async Task CheckOrderAsync(SalesOrder order, string token, CancellationToken cancellationToken)
    {
        order.MpCheckedAt = DateTime.UtcNow;
        order.MpExpectedNet = Expected(order);

        if (order.MpPaymentId is null)
        {
            var (ok, _, content) = await GetAsync($"{MlBase}/orders/{Uri.EscapeDataString(order.ExternalOrderId)}", token, cancellationToken);
            if (!ok) { order.MpError = "Mercado Libre no regresó la venta."; return; }
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("payments", out var payments) && payments.ValueKind == JsonValueKind.Array)
                    order.MpPaymentId = payments.EnumerateArray()
                        .Where(p => Str(p, "status") is "approved" or null)
                        .Select(p => Str(p, "id"))
                        .FirstOrDefault(id => id is not null);
            }
            catch (JsonException) { }
            if (order.MpPaymentId is null) { order.MpError = "La venta no tiene pago aprobado."; return; }
        }

        var (paid, status, payment) = await GetAsync($"{MpBase}/v1/payments/{Uri.EscapeDataString(order.MpPaymentId)}", token, cancellationToken);
        if (!paid)
        {
            order.MpError = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Mercado Pago no dejó leer el pago (permiso de la app)."
                : status == HttpStatusCode.NotFound ? "Mercado Pago no encontró el pago." : "Mercado Pago no respondió.";
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(payment);
            var root = document.RootElement;
            if (root.TryGetProperty("transaction_details", out var details))
                order.MpNetReceived = Dec(details, "net_received_amount");
            order.MpReleaseDate = Date(Str(root, "money_release_date"));
            order.MpReleaseStatus = Str(root, "money_release_status")
                ?? (order.MpReleaseDate is { } release && release <= DateTime.UtcNow ? "released" : "pending");
            order.MpError = null;
        }
        catch (JsonException)
        {
            order.MpError = "No entendí la respuesta de Mercado Pago.";
        }
    }

    public async Task<MoneySummary> SummaryAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-60);
        var orders = await _context.SalesOrders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= since)
            .OrderByDescending(o => o.DateCreated)
            .ToListAsync(cancellationToken);
        var checkedOrders = orders.Where(o => o.MpNetReceived is not null).ToList();
        var pending = checkedOrders.Where(o => o.MpReleaseStatus != "released").ToList();
        var monthStart = new DateTime(now.AddHours(-7).Year, now.AddHours(-7).Month, 1, 7, 0, 0, DateTimeKind.Utc);

        return new MoneySummary
        {
            Pending = pending.Sum(o => o.MpNetReceived!.Value),
            ReleasedThisMonth = checkedOrders.Where(o => o.MpReleaseStatus == "released" && o.MpReleaseDate >= monthStart).Sum(o => o.MpNetReceived!.Value),
            Upcoming = pending.Where(o => o.MpReleaseDate is not null)
                .GroupBy(o => DateOnly.FromDateTime(o.MpReleaseDate!.Value.AddHours(-7)))
                .OrderBy(g => g.Key).Take(10)
                .Select(g => (g.Key, g.Sum(o => o.MpNetReceived!.Value), g.Count())).ToList(),
            Differences = checkedOrders.Where(HasDifference).ToList(),
            Recent = orders.Take(40).ToList(),
            NotChecked = orders.Count(o => o.MpCheckedAt is null),
            Error = orders.Where(o => o.MpError is not null).Select(o => o.MpError).FirstOrDefault()
        };
    }

    private async Task<(bool Ok, HttpStatusCode Status, string Content)> GetAsync(string url, string token, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogInformation("GET {Url} returned {Status}.", url, (int)response.StatusCode);
            return (response.IsSuccessStatusCode, response.StatusCode, content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "GET {Url} failed.", url);
            return (false, HttpStatusCode.ServiceUnavailable, string.Empty);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTime? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
}
