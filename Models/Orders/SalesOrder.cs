using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.Models.Orders;

public class SalesOrder
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public int MarketplaceAccountId { get; set; }
    public MarketplaceAccount MarketplaceAccount { get; set; } = null!;

    [Required, MaxLength(50)]
    public string Marketplace { get; set; } = "MercadoLibre";

    [Required, MaxLength(100)]
    public string ExternalOrderId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ExternalPackId { get; set; }

    [Required, MaxLength(50)]
    public string Status { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? StatusDetail { get; set; }

    [MaxLength(20)]
    public string? CurrencyId { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal TotalAmount { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal PaidAmount { get; set; }

    [MaxLength(100)]
    public string? BuyerId { get; set; }

    [MaxLength(200)]
    public string? BuyerNickname { get; set; }

    [MaxLength(100)]
    public string? ShippingId { get; set; }

    public DateTime DateCreated { get; set; }
    public DateTime? DateClosed { get; set; }
    public DateTime LastUpdated { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SalesOrderItem> Items { get; set; } = new List<SalesOrderItem>();
}
