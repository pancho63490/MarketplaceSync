using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MarketplaceSync.Web.Models.Orders;

public class SalesOrderItem
{
    public int Id { get; set; }
    public int SalesOrderId { get; set; }
    public SalesOrder SalesOrder { get; set; } = null!;

    [Required, MaxLength(100)]
    public string ExternalItemId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ExternalVariationId { get; set; }

    [MaxLength(500)]
    public string? Title { get; set; }

    [MaxLength(100)]
    public string? SellerSku { get; set; }

    public int Quantity { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal? SaleFee { get; set; }

    [MaxLength(20)]
    public string? CurrencyId { get; set; }
}
