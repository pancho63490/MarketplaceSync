using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Inventory;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Inventory;

public sealed class InventoryService : IInventoryService
{
    private static readonly HashSet<string> FinalCancelledStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "cancelled", "invalid" };

    private readonly AppDbContext _dbContext;
    private readonly ILogger<InventoryService> _logger;

    private readonly MarketplaceSync.Services.MercadoLibre.IStockSyncScheduler _stockSync;

    public InventoryService(
        AppDbContext dbContext,
        ILogger<InventoryService> logger,
        MarketplaceSync.Services.MercadoLibre.IStockSyncScheduler stockSync)
    {
        _stockSync = stockSync;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<InventoryReconciliationResult> ReconcileOrderAsync(
        int salesOrderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.SalesOrders
            .AsNoTracking()
            .Include(x => x.Items)
            .SingleAsync(x => x.Id == salesOrderId, cancellationToken);

        var paid = string.Equals(order.Status, "paid", StringComparison.OrdinalIgnoreCase);
        var cancelled = FinalCancelledStatuses.Contains(order.Status);
        if (!paid && !cancelled)
            return new InventoryReconciliationResult(0, 0, 0);

        var quantitiesBySku = order.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.SellerSku) && x.Quantity > 0)
            .GroupBy(x => x.SellerSku!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.Quantity), StringComparer.OrdinalIgnoreCase);

        var currentDeltasByVariant = await _dbContext.InventoryMovements
            .AsNoTracking()
            .Where(x => x.SalesOrderId == order.Id &&
                        (x.Type == "OrderSale" || x.Type == "OrderReversal"))
            .GroupBy(x => x.ProductVariantId)
            .Select(group => new { VariantId = group.Key, Delta = group.Sum(x => x.QuantityDelta) })
            .ToDictionaryAsync(x => x.VariantId, x => x.Delta, cancellationToken);
        var normalizedSkus = quantitiesBySku.Keys.Select(x => x.ToUpperInvariant()).ToArray();
        var existingVariantIds = currentDeltasByVariant.Keys.ToArray();
        var variants = await _dbContext.ProductVariants
            .Where(x => x.OrganizationId == order.OrganizationId &&
                        (normalizedSkus.Contains(x.Sku.ToUpper()) || existingVariantIds.Contains(x.Id)))
            .ToListAsync(cancellationToken);
        var variantsBySku = variants.ToDictionary(x => x.Sku, StringComparer.OrdinalIgnoreCase);
        var variantsById = variants.ToDictionary(x => x.Id);
        var targetDeltasByVariant = new Dictionary<int, int>();
        var movementsCreated = 0;
        var unitsChanged = 0;
        var unmatchedSkus = 0;

        foreach (var (sku, orderedQuantity) in quantitiesBySku)
        {
            if (!variantsBySku.TryGetValue(sku, out var variant))
            {
                unmatchedSkus++;
                continue;
            }

            targetDeltasByVariant[variant.Id] = paid ? -orderedQuantity : 0;
        }

        foreach (var existingVariantId in existingVariantIds)
            targetDeltasByVariant.TryAdd(existingVariantId, 0);

        foreach (var (variantId, targetOrderDelta) in targetDeltasByVariant)
        {
            if (!variantsById.TryGetValue(variantId, out var variant))
                continue;

            var currentOrderDelta = currentDeltasByVariant.GetValueOrDefault(variantId);
            var adjustment = targetOrderDelta - currentOrderDelta;
            if (adjustment == 0)
                continue;

            variant.Stock += adjustment;
            variant.UpdatedAt = DateTime.UtcNow;
            var movementType = adjustment < 0 ? "OrderSale" : "OrderReversal";
            var referenceKey = string.Join(':',
                "order",
                order.MarketplaceAccountId,
                order.ExternalOrderId,
                variant.Id,
                order.LastUpdated.Ticks,
                targetOrderDelta);

            _dbContext.InventoryMovements.Add(new InventoryMovement
            {
                OrganizationId = order.OrganizationId,
                ProductVariantId = variant.Id,
                SalesOrderId = order.Id,
                Type = movementType,
                QuantityDelta = adjustment,
                StockAfter = variant.Stock,
                ReferenceKey = referenceKey,
                Notes = $"Pedido Mercado Libre #{order.ExternalOrderId}",
                CreatedAt = DateTime.UtcNow
            });
            movementsCreated++;
            unitsChanged += adjustment;
        }

        if (movementsCreated > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _stockSync.RequestPush(targetDeltasByVariant.Keys
                .Where(variantsById.ContainsKey)
                .Select(id => variantsById[id].ProductId));
        }

        if (unmatchedSkus > 0)
        {
            _logger.LogWarning(
                "Order {SalesOrderId} has {UnmatchedSkus} SKU values without a matching product variant.",
                order.Id,
                unmatchedSkus);
        }

        return new InventoryReconciliationResult(movementsCreated, unitsChanged, unmatchedSkus);
    }
}
