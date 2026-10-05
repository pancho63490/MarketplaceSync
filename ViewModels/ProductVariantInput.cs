using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.ViewModels;

public sealed class ProductVariantInput
{
    public int ProductId { get; set; }
    public int? Id { get; set; }

    [Required, StringLength(100)]
    [RegularExpression(@"^[A-Za-z0-9._/-]+$", ErrorMessage = "El SKU solo puede incluir letras, números, punto, guion y diagonal.")]
    public string Sku { get; set; } = string.Empty;

    [Required, StringLength(500, MinimumLength = 3)]
    public string Attributes { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Barcode { get; set; }

    [Range(typeof(decimal), "0.01", "999999999")]
    public decimal? Price { get; set; }

    [Range(0, 999999)]
    public int Stock { get; set; }

    public bool IsActive { get; set; } = true;
}
