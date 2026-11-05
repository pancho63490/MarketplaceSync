using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Models;

namespace MarketplaceSync.Web.ViewModels.Inventory;

public sealed class InventoryIndexViewModel
{
    public IReadOnlyList<InventoryVariantRowViewModel> Variants { get; init; }
        = Array.Empty<InventoryVariantRowViewModel>();
    public IReadOnlyList<InventoryMovement> RecentMovements { get; init; }
        = Array.Empty<InventoryMovement>();
    public string? Search { get; init; }
    public bool LowStockOnly { get; init; }
    public int TotalVariants { get; init; }
    public int TotalUnits { get; init; }
    public int LowStockVariants { get; init; }
    public int OutOfStockVariants { get; init; }
    public IReadOnlyList<MarketplaceSync.Services.Inventory.ExpiringLot> ExpiringLots { get; init; }
        = Array.Empty<MarketplaceSync.Services.Inventory.ExpiringLot>();
    public IReadOnlyDictionary<int, (DateOnly ExpiresOn, int Units, int LotUnits)> NextExpiryByVariant { get; init; }
        = new Dictionary<int, (DateOnly ExpiresOn, int Units, int LotUnits)>();
}

public sealed class InventoryVariantRowViewModel
{
    public ProductVariant Variant { get; init; } = null!;
    public int NetSoldUnits { get; init; }
}
