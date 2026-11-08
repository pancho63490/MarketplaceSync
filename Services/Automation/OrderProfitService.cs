using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Orders;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Automation;

/// <summary>
/// Ganancia de cada venta pagada: cobrado − comisión real (sale_fee del pedido) − envío real que pagó el vendedor
/// (GET /shipments/{id}/costs) − retención de ISR e IVA (tasa de Costos de cruce: con o sin RFC) − costo puesto en Juárez.
/// Se recalcula unos días mientras Mercado Libre ajusta el envío; luego queda fija.
/// </summary>
public sealed class OrderProfitService
{
    private const int BatchSize = 40;
    private static readonly TimeSpan Settle = TimeSpan.FromDays(10);

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly ICrossBorderPricingService _pricing;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrderProfitService> _logger;

    public OrderProfitService(
        AppDbContext context,
        IMercadoLibreAuthService auth,
        ICrossBorderPricingService pricing,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OrderProfitService> logger)
    {
        _context = context;
        _auth = auth;
        _pricing = pricing;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    /// <summary>Calcula las ventas pendientes (nuevas o recientes). Regresa cuántas actualizó.</summary>
    public async Task<int> ComputePendingAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var recalcBefore = now.AddHours(-12);
        var orders = await _context.SalesOrders
            .Include(o => o.Items)
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= now.AddDays(-90) &&
                        (o.ProfitComputedAt == null ||
                         (o.DateCreated >= now - Settle && o.ProfitComputedAt < recalcBefore)))
            .OrderByDescending(o => o.DateCreated)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (orders.Count == 0)
            return 0;

        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var landed = await LandedCostsAsync(org, orders.SelectMany(o => o.Items).ToList(), settings, cancellationToken);
        var tokens = new Dictionary<int, string?>();

        foreach (var order in orders)
        {
            if (!tokens.TryGetValue(order.MarketplaceAccountId, out var token))
            {
                try { token = await _auth.GetValidAccessTokenAsync(org, order.MarketplaceAccountId); }
                catch (Exception exception) { _logger.LogWarning(exception, "No token for profit of account {Account}.", order.MarketplaceAccountId); token = null; }
                tokens[order.MarketplaceAccountId] = token;
            }

            decimal? shipping = null;
            if (token is not null && !string.IsNullOrWhiteSpace(order.ShippingId))
                shipping = await SellerShippingCostAsync(token, order.ShippingId!, cancellationToken);

            Compute(order, landed, settings, shipping);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return orders.Count;
    }

    /// <summary>La cuenta de una venta (también la usa la vista para el desglose).</summary>
    public static void Compute(
        SalesOrder order,
        IReadOnlyDictionary<string, decimal?> landedByItem,
        MarketplaceSync.Web.Models.CrossBorder.CrossBorderSettings settings,
        decimal? realShipping)
    {
        var fees = order.Items.Sum(i => (i.SaleFee ?? 0) * i.Quantity);
        var shippingIsReal = realShipping.HasValue;
        var shipping = realShipping ?? (order.TotalAmount >= 299m && order.ShippingId is not null ? settings.EstimatedShippingMxn : 0m);
        var tax = Math.Round(order.TotalAmount * settings.PlatformTaxWithholdingPercent / 100m, 2);

        decimal? cost = 0m;
        foreach (var item in order.Items)
        {
            var unit = landedByItem.GetValueOrDefault(item.ExternalItemId);
            if (unit is null) { cost = null; break; }
            cost += unit.Value * item.Quantity;
        }

        order.SellerShippingCostMxn = shipping;
        order.ShippingCostIsReal = shippingIsReal;
        order.TaxWithheldMxn = tax;
        order.ProductCostMxn = cost is null ? null : Math.Round(cost.Value, 2);
        order.RealProfitMxn = cost is null ? null : Math.Round(order.TotalAmount - fees - shipping - tax - cost.Value, 2);
        order.ProfitComputedAt = DateTime.UtcNow;
    }

    /// <summary>Costo puesto en Juárez por pieza, por número de publicación MLM (null = sin costo capturado).</summary>
    public async Task<Dictionary<string, decimal?>> LandedCostsAsync(
        Guid org, List<SalesOrderItem> items, MarketplaceSync.Web.Models.CrossBorder.CrossBorderSettings settings, CancellationToken cancellationToken)
    {
        var itemIds = items.Select(i => i.ExternalItemId).Distinct().ToList();
        var products = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.ExternalItemId != null && itemIds.Contains(p.ExternalItemId))
            .Select(p => new { p.ExternalItemId, p.Product.SourcePrice, p.Product.SourceCurrency, p.Product.SourceMarketplace, p.Product.RealLandedCostMxn })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in itemIds)
        {
            var product = products.FirstOrDefault(p => string.Equals(p.ExternalItemId, id, StringComparison.OrdinalIgnoreCase));
            result[id] = product is null ? null
                : ProductCost.Landed(product.RealLandedCostMxn, product.SourcePrice, product.SourceCurrency, product.SourceMarketplace, settings, _pricing);
        }
        return result;
    }

    private async Task<decimal?> SellerShippingCostAsync(string token, string shipmentId, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/shipments/{Uri.EscapeDataString(shipmentId)}/costs");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("senders", out var senders) || senders.ValueKind != JsonValueKind.Array)
                return null;
            decimal total = 0;
            var any = false;
            foreach (var sender in senders.EnumerateArray())
            {
                if (sender.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number)
                {
                    total += cost.GetDecimal();
                    any = true;
                }
            }
            return any ? Math.Round(total, 2) : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Could not read shipping costs of shipment {Shipment}.", shipmentId);
            return null;
        }
    }
}
