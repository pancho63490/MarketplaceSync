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

    /// <summary>Estado del envío en Mercado Libre: pending, handling, ready_to_ship, shipped, delivered, not_delivered, cancelled.</summary>
    [MaxLength(40)]
    public string? ShippingStatus { get; set; }

    /// <summary>Detalle: ready_to_print, printed, in_hub, waiting_for_withdrawal…</summary>
    [MaxLength(60)]
    public string? ShippingSubstatus { get; set; }

    /// <summary>fulfillment (Full), self_service (Flex), cross_docking, drop_off, xd_drop_off…</summary>
    [MaxLength(40)]
    public string? ShippingLogistic { get; set; }

    /// <summary>Hora límite para despachar sin que cuente como demora.</summary>
    public DateTime? ShippingDeadline { get; set; }

    public DateTime? ShippingCheckedAt { get; set; }

    /// <summary>Cuándo se confirmó con el escáner que el paquete lleva las piezas correctas.</summary>
    public DateTime? PackedAt { get; set; }

    /// <summary>Cuándo se imprimió la etiqueta desde la app (lote).</summary>
    public DateTime? LabelPrintedAt { get; set; }

    /// <summary>Mensaje de gracias automático: cuándo se intentó y cómo salió (sent, failed).</summary>
    public DateTime? ThankYouSentAt { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(20)]
    public string? ThankYouStatus { get; set; }

    /// <summary>Pagado, con envío por Mercado Envíos que falta despachar (no Full).</summary>
    public bool NeedsShipping =>
        Status == "paid" && ShippingLogistic != "fulfillment" &&
        ShippingStatus is "pending" or "handling" or "ready_to_ship" &&
        ShippingSubstatus is not ("in_hub" or "in_packing_list" or "picked_up" or "dropped_off" or "in_warehouse");

    // ── Ganancia de la venta (OrderProfitService) ──

    /// <summary>Envío que pagó el vendedor según Mercado Libre (GET /shipments/{id}/costs, senders[].cost).</summary>
    public decimal? SellerShippingCostMxn { get; set; }

    /// <summary>Retención de ISR + IVA calculada con la tasa de Costos de cruce (con o sin RFC).</summary>
    public decimal? TaxWithheldMxn { get; set; }

    /// <summary>Costo puesto en Juárez de lo vendido (con el tipo de cambio del día del cálculo).</summary>
    public decimal? ProductCostMxn { get; set; }

    /// <summary>Cobrado − comisión − envío − retención − costo. Null si falta el costo de algún producto.</summary>
    public decimal? RealProfitMxn { get; set; }

    /// <summary>true si el envío salió de Mercado Libre; false si se usó el estimado de Costos de cruce.</summary>
    public bool? ShippingCostIsReal { get; set; }

    public DateTime? ProfitComputedAt { get; set; }

    // ── Factura ──

    /// <summary>CFDI que ampara esta venta (individual o global).</summary>
    public int? SalesInvoiceId { get; set; }

    /// <summary>RFC que capturó el comprador en Mercado Libre (null = no pidió factura o no se ha revisado).</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(13)]
    public string? BuyerRfc { get; set; }

    /// <summary>Última vez que se leyeron sus datos de facturación en Mercado Libre.</summary>
    public DateTime? BillingCheckedAt { get; set; }

    // ── Mercado Pago ──

    [System.ComponentModel.DataAnnotations.MaxLength(30)]
    public string? MpPaymentId { get; set; }

    /// <summary>Lo que Mercado Pago dice que te deja la venta (neto después de comisiones, envío y retenciones).</summary>
    public decimal? MpNetReceived { get; set; }

    /// <summary>Lo que la app esperaba recibir (cobrado − comisión − envío − retención).</summary>
    public decimal? MpExpectedNet { get; set; }

    public DateTime? MpReleaseDate { get; set; }

    /// <summary>released, pending u otro estado de liberación.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(20)]
    public string? MpReleaseStatus { get; set; }

    public DateTime? MpCheckedAt { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string? MpError { get; set; }

    public DateTime DateCreated { get; set; }
    public DateTime? DateClosed { get; set; }
    public DateTime LastUpdated { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SalesOrderItem> Items { get; set; } = new List<SalesOrderItem>();
}

/// <summary>Consultas de envío que EF traduce a SQL (pedidos por despachar y atrasados).</summary>
public static class SalesOrderShippingQueries
{
    private static readonly string[] Open = { "pending", "handling", "ready_to_ship" };
    private static readonly string[] AlreadyHandedOver = { "in_hub", "in_packing_list", "picked_up", "dropped_off", "in_warehouse" };

    /// <summary>Pagados con Mercado Envíos que falta despachar (Full no cuenta: lo envía Mercado Libre).</summary>
    public static IQueryable<SalesOrder> ToShip(this IQueryable<SalesOrder> query) =>
        query.Where(o => o.Status == "paid" &&
                         (o.ShippingLogistic == null || o.ShippingLogistic != "fulfillment") &&
                         o.ShippingStatus != null && Open.Contains(o.ShippingStatus) &&
                         (o.ShippingSubstatus == null || !AlreadyHandedOver.Contains(o.ShippingSubstatus)));

    /// <summary>Por despachar y ya pasó la hora límite: cuenta como despacho con demora.</summary>
    public static IQueryable<SalesOrder> LateToShip(this IQueryable<SalesOrder> query, DateTime nowUtc) =>
        query.ToShip().Where(o => o.ShippingDeadline != null && o.ShippingDeadline < nowUtc);
}
