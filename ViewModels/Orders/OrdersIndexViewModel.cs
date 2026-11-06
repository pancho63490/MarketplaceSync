using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.ViewModels.Orders;

public sealed class OrdersIndexViewModel
{
    public IReadOnlyList<SalesOrder> Orders { get; init; } = Array.Empty<SalesOrder>();
    public IReadOnlyList<string> Statuses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MarketplaceAccount> Accounts { get; init; } = Array.Empty<MarketplaceAccount>();
    public string? SelectedStatus { get; init; }
    /// <summary>null = todos, "toship" = por enviar, "late" = atrasados.</summary>
    public string? View { get; init; }
    public int ToShipCount { get; init; }
    public int LateCount { get; init; }
    public int? SelectedAccountId { get; init; }
    public string? Search { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public int TotalOrders { get; init; }
    public int FilteredOrders { get; init; }
    public int PaidOrders { get; init; }
    public int PendingOrders { get; init; }
    public int CancelledOrders { get; init; }
    public decimal TotalSales { get; init; }
    public int ConnectedAccounts { get; init; }
    public DateTime? LastSyncAt { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(FilteredOrders / (double)PageSize));
}
