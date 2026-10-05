using MarketplaceSync.Web.Models;

namespace MarketplaceSync.Web.ViewModels;

public sealed class ProductVariantsViewModel
{
    public int ProductId { get; init; }
    public string ProductTitle { get; init; } = string.Empty;
    public ProductVariantInput NewVariant { get; init; } = new();
    public IReadOnlyList<ProductVariantRowViewModel> Variants { get; init; } = Array.Empty<ProductVariantRowViewModel>();
}

public sealed class ProductVariantRowViewModel
{
    public int Id { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Attributes { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public decimal? Price { get; init; }
    public int Stock { get; init; }
    public bool IsActive { get; init; }
    public bool IsPublished { get; init; }
}
