using MarketplaceSync.Web.Models.Orders;

namespace MarketplaceSync.Web.ViewModels.Orders;

public sealed class OrdersIndexViewModel
{
    public IReadOnlyList<SalesOrder> Orders { get; init; } = Array.Empty<SalesOrder>();
    public IReadOnlyList<string> Statuses { get; init; } = Array.Empty<string>();
    public string? SelectedStatus { get; init; }
    public int TotalOrders { get; init; }
    public int ConnectedAccounts { get; init; }
    public DateTime? LastSyncAt { get; init; }
}
