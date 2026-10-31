using System.ComponentModel.DataAnnotations;
using MarketplaceSync.Web.Models.Orders;

namespace MarketplaceSync.Web.Models.Inventory;

public sealed class InventoryMovement
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public int ProductVariantId { get; set; }
    public ProductVariant ProductVariant { get; set; } = null!;
    public int? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }

    [Required, MaxLength(40)]
    public string Type { get; set; } = string.Empty;

    public int QuantityDelta { get; set; }
    public int StockAfter { get; set; }

    [MaxLength(300)]
    public string? ReferenceKey { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
