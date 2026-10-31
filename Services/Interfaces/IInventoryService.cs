namespace MarketplaceSync.Services.Interfaces;

public interface IInventoryService
{
    Task<InventoryReconciliationResult> ReconcileOrderAsync(
        int salesOrderId,
        CancellationToken cancellationToken = default);
}

public sealed record InventoryReconciliationResult(
    int MovementsCreated,
    int UnitsChanged,
    int UnmatchedSkus);
