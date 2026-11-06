using MarketplaceSync.Services.Inventory;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Models.PostSale;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.PostSale;

public sealed record ReturnLine(string? Title, string? Sku, int Quantity, int? VariantId, int? Stock, bool AlreadyBack);

/// <summary>
/// Devoluciones: cuando la pieza regresa, con un clic vuelve al inventario (movimiento "ReturnRestock")
/// y el stock nuevo se manda a Mercado Libre. Si llegó dañada se registra sin sumar.
/// La conciliación de pedidos cuenta estos movimientos, así que una cancelación posterior no la suma dos veces.
/// </summary>
public sealed class ReturnRestockService
{
    public const string MovementType = "ReturnRestock";

    private readonly AppDbContext _context;
    private readonly InventoryLotService _lots;
    private readonly IStockSyncScheduler _stockSync;

    public ReturnRestockService(AppDbContext context, InventoryLotService lots, IStockSyncScheduler stockSync)
    {
        _context = context;
        _lots = lots;
        _stockSync = stockSync;
    }

    public static readonly (string Id, string Label, string Help)[] Conditions =
    {
        ("good", "Llegó bien: vuelve a la venta", "Se suma al stock y se actualiza en Mercado Libre."),
        ("damaged", "Llegó dañada o incompleta", "Se registra la devolución pero no se suma al stock.")
    };

    public static string ConditionLabel(string? id) => id switch
    {
        "good" => "regresó al inventario",
        "damaged" => "llegó dañada (no se sumó)",
        _ => "registrada"
    };

    /// <summary>Lo que trae el pedido del reclamo y si cada pieza ya regresó (por cancelación o devolución).</summary>
    public async Task<IReadOnlyList<ReturnLine>> GetLinesAsync(Guid org, OrderClaim claim, CancellationToken cancellationToken = default)
    {
        if (claim.SalesOrderId is not { } orderId)
            return Array.Empty<ReturnLine>();
        var items = await _context.SalesOrderItems.AsNoTracking().Where(i => i.SalesOrderId == orderId).ToListAsync(cancellationToken);
        var skus = items.Where(i => !string.IsNullOrWhiteSpace(i.SellerSku)).Select(i => i.SellerSku!.Trim().ToUpper()).ToList();
        var variants = await _context.ProductVariants.AsNoTracking()
            .Where(v => v.OrganizationId == org && skus.Contains(v.Sku.ToUpper()))
            .ToListAsync(cancellationToken);
        var net = await NetByVariantAsync(orderId, cancellationToken);
        return items.Select(i =>
        {
            var v = variants.FirstOrDefault(x => string.Equals(x.Sku, i.SellerSku?.Trim(), StringComparison.OrdinalIgnoreCase));
            var back = v is not null && net.TryGetValue(v.Id, out var d) && d >= 0;
            return new ReturnLine(i.Title, i.SellerSku, i.Quantity, v?.Id, v?.Stock, back);
        }).ToList();
    }

    /// <summary>Registra la devolución. Devuelve (mensaje, error).</summary>
    public async Task<(string? Message, string? Error)> RestockAsync(Guid org, long claimId, string condition, string? userId, CancellationToken cancellationToken = default)
    {
        var claim = await _context.OrderClaims.FirstOrDefaultAsync(c => c.Id == claimId && c.OrganizationId == org, cancellationToken)
                    ?? throw new KeyNotFoundException();
        if (claim.ReturnRestockedAt is not null)
            return (null, "Esta devolución ya se había registrado.");
        if (condition is not ("good" or "damaged"))
            return (null, "Elige cómo llegó la pieza.");
        if (claim.SalesOrderId is not { } orderId)
            return (null, "Este reclamo no está ligado a un pedido de la app; sincroniza los pedidos.");

        var order = await _context.SalesOrders.AsNoTracking().Include(o => o.Items)
            .FirstAsync(o => o.Id == orderId, cancellationToken);
        var units = 0;
        var touched = new List<int>();
        var unmatched = 0;
        var alreadyBack = 0;
        var noSale = 0;

        if (condition == "good")
        {
            var net = await NetByVariantAsync(orderId, cancellationToken);
            foreach (var group in order.Items.Where(i => i.Quantity > 0)
                         .GroupBy(i => i.SellerSku?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var variant = group.Key.Length == 0 ? null : await _context.ProductVariants
                    .FirstOrDefaultAsync(v => v.OrganizationId == org && v.Sku.ToUpper() == group.Key.ToUpper(), cancellationToken);
                if (variant is null) { unmatched++; continue; }

                if (!net.ContainsKey(variant.Id)) { noSale++; continue; }

                // Solo lo que sigue fuera: si el pedido se canceló, la pieza ya había regresado.
                var outstanding = -Math.Min(0, net.GetValueOrDefault(variant.Id));
                var qty = Math.Min(group.Sum(i => i.Quantity), outstanding);
                if (qty <= 0) { alreadyBack++; continue; }

                variant.Stock += qty;
                variant.UpdatedAt = DateTime.UtcNow;
                await _lots.ApplyAsync(org, variant.Id, qty, null, null, "Devolución", cancellationToken);
                _context.InventoryMovements.Add(new InventoryMovement
                {
                    OrganizationId = org,
                    ProductVariantId = variant.Id,
                    SalesOrderId = order.Id,
                    Type = MovementType,
                    QuantityDelta = qty,
                    StockAfter = variant.Stock,
                    ReferenceKey = $"return:{claim.ExternalId}:{variant.Id}",
                    Notes = $"Devolución del pedido #{order.ExternalOrderId} (reclamo {claim.ExternalId})",
                    CreatedByUserId = userId,
                    CreatedAt = DateTime.UtcNow
                });
                units += qty;
                touched.Add(variant.ProductId);
            }
        }

        claim.ReturnRestockedAt = DateTime.UtcNow;
        claim.ReturnCondition = condition;
        claim.ReturnUnits = units;
        await _context.SaveChangesAsync(cancellationToken);
        if (touched.Count > 0)
            _stockSync.RequestPush(touched.Distinct());

        if (condition == "damaged")
            return ("Devolución registrada como dañada: no se sumó al stock.", null);
        var message = units > 0
            ? $"Listo: {units} {(units == 1 ? "pieza regresó" : "piezas regresaron")} al inventario y el stock se actualiza en Mercado Libre."
            : "Devolución registrada.";
        if (alreadyBack > 0)
            message += " Lo que ya había regresado cuando se canceló el pedido no se sumó otra vez.";
        if (noSale > 0)
            message += " Alguna pieza no se descontó del stock cuando se vendió, así que tampoco se sumó ahora.";
        if (unmatched > 0)
            message += " Hay piezas sin SKU en tu inventario: súmalas a mano en Inventario.";
        return (message, null);
    }

    /// <summary>Suma de los movimientos del pedido por variante (venta negativa, cancelación y devolución positivas).</summary>
    private async Task<Dictionary<int, int>> NetByVariantAsync(int orderId, CancellationToken cancellationToken) =>
        await _context.InventoryMovements.AsNoTracking()
            .Where(m => m.SalesOrderId == orderId && (m.Type == "OrderSale" || m.Type == "OrderReversal" || m.Type == MovementType))
            .GroupBy(m => m.ProductVariantId)
            .Select(g => new { g.Key, Net = g.Sum(m => m.QuantityDelta) })
            .ToDictionaryAsync(x => x.Key, x => x.Net, cancellationToken);
}
