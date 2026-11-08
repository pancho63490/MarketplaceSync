using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Models.Inventory;

namespace MarketplaceSync.Web.Models;

public class ProductVariant
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Sku { get; set; } = string.Empty;

    [Required]
    public string AttributesJson { get; set; } = "{}";

    [MaxLength(100)]
    public string? Barcode { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal? Price { get; set; }

    public int Stock { get; set; }
    public int LowStockThreshold { get; set; } = 5;

    /// <summary>Piezas que no se publican (para no sobrevender); null = la reserva general de Automatización.</summary>
    public int? StockReserve { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<MarketplacePublicationVariant> MarketplacePublications { get; set; }
        = new List<MarketplacePublicationVariant>();
    public ICollection<InventoryMovement> InventoryMovements { get; set; }
        = new List<InventoryMovement>();
}
