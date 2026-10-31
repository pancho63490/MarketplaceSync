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
}

public sealed class InventoryVariantRowViewModel
{
    public ProductVariant Variant { get; init; } = null!;
    public int NetSoldUnits { get; init; }
}
