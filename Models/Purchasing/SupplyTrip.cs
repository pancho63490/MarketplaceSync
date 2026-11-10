using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MarketplaceSync.Web.Models.Purchasing;

/// <summary>Un viaje a El Paso: lo que se compró y lo que costó traerlo (gasolina, cruce, impuestos).</summary>
public sealed class SupplyTrip
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    public DateOnly Date { get; set; }

    /// <summary>planning (comprando / en camino) o received (ya está en Juárez).</summary>
    [MaxLength(12)]
    public string Status { get; set; } = SupplyTripStatus.Planning;

    /// <summary>Tipo de cambio que pagaste (tarjeta o casa de cambio).</summary>
    [Column(TypeName = "numeric(10,4)")]
    public decimal ExchangeRate { get; set; }

    [Column(TypeName = "numeric(12,2)")]
    public decimal FuelMxn { get; set; }

    /// <summary>Agente aduanal, comercializadora o cruce.</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal CrossingMxn { get; set; }

    /// <summary>IGI, DTA, IVA pagados en la aduana.</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal CustomsTaxesMxn { get; set; }

    /// <summary>Puentes, estacionamiento, comidas, paquetería en El Paso…</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal OtherMxn { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>Lista de compra semanal de la que salieron las piezas (se marca recibida al recibir el viaje).</summary>
    public int? PurchaseListId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReceivedAt { get; set; }

    public List<SupplyTripItem> Items { get; set; } = new();

    [NotMapped]
    public decimal ExtrasMxn => FuelMxn + CrossingMxn + CustomsTaxesMxn + OtherMxn;
}

public static class SupplyTripStatus
{
    public const string Planning = "planning";
    public const string Received = "received";
}

public sealed class SupplyTripItem
{
    public int Id { get; set; }
    public int SupplyTripId { get; set; }
    public SupplyTrip SupplyTrip { get; set; } = null!;

    /// <summary>SKU de la app (si es pieza que ya tienes); null = pieza nueva sin SKU.</summary>
    public int? ProductVariantId { get; set; }

    /// <summary>Producto de la app aunque aún no tenga SKU (importado por link): al recibir se le guarda el costo real.</summary>
    public int? ProductId { get; set; }

    /// <summary>Link de la tienda donde se compró (Amazon, eBay, Walmart…).</summary>
    [MaxLength(1000)]
    public string? SourceUrl { get; set; }

    [MaxLength(300)]
    public string Description { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    [Column(TypeName = "numeric(12,2)")]
    public decimal UnitCostUsd { get; set; }

    /// <summary>Envío hasta El Paso de este renglón (total, US$).</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal ShippingUsd { get; set; }

    [MaxLength(60)]
    public string? Store { get; set; }

    /// <summary>Costo real por pieza puesto en Juárez (se calcula al recibir).</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal? RealUnitCostMxn { get; set; }

    /// <summary>Lo que estimaba Costos de cruce para esa pieza (para comparar).</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal? EstimatedUnitCostMxn { get; set; }
}

/// <summary>Un paquete que viene a tu dirección de El Paso (UPS, USPS, FedEx, Amazon…).</summary>
public sealed class InboundPackage
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    [MaxLength(60)]
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>ups, usps, fedex, amazon, dhl u other.</summary>
    [MaxLength(12)]
    public string Carrier { get; set; } = "other";

    [MaxLength(200)]
    public string? Description { get; set; }

    public int? SupplyTripId { get; set; }

    /// <summary>NotFound, InfoReceived, InTransit, OutForDelivery, AvailableForPickup, Delivered, DeliveryFailure, Exception, Expired.</summary>
    [MaxLength(24)]
    public string Status { get; set; } = "NotFound";

    [MaxLength(300)]
    public string? LastEvent { get; set; }

    [MaxLength(120)]
    public string? Location { get; set; }

    public DateTime? LastEventAt { get; set; }
    public DateOnly? EstimatedDelivery { get; set; }
    public DateTime? DeliveredAt { get; set; }

    /// <summary>Ya se dio de alta en el servicio de rastreo.</summary>
    public bool Registered { get; set; }

    public DateTime? LastCheckedAt { get; set; }

    /// <summary>Último estado que se avisó por Telegram (para no repetir).</summary>
    [MaxLength(24)]
    public string? NotifiedStatus { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
}
