using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Inventory;

/// <summary>
/// Lote de un SKU con fecha de caducidad (dulces, bebidas, químicos, baterías…).
/// El stock oficial sigue en ProductVariant.Stock; los lotes dicen qué piezas caducan primero.
/// </summary>
public sealed class InventoryLot
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public int ProductVariantId { get; set; }
    public ProductVariant ProductVariant { get; set; } = null!;

    [MaxLength(50)]
    public string? LotCode { get; set; }

    public DateOnly ExpiresOn { get; set; }
    public int QuantityReceived { get; set; }
    public int QuantityRemaining { get; set; }

    /// <summary>Compra, Ajuste.</summary>
    [MaxLength(30)]
    public string Source { get; set; } = "Ajuste";

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? WrittenOffAt { get; set; }
}
