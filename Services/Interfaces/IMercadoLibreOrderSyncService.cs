namespace MarketplaceSync.Services.Interfaces;

public interface IMercadoLibreOrderSyncService
{
    Task<OrderSyncSummary> SyncAllAccountsAsync(
        Guid? organizationId = null,
        CancellationToken cancellationToken = default);
}

public sealed class OrderSyncSummary
{
    public int AccountsProcessed { get; set; }
    public int AccountsFailed { get; set; }
    public int OrdersCreated { get; set; }
    public int OrdersUpdated { get; set; }
}
