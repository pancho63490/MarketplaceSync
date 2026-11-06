using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.PostSale;

/// <summary>Mensaje de postventa entre el vendedor y el comprador de un paquete (pack) de Mercado Libre.</summary>
public sealed class OrderMessage
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int MarketplaceAccountId { get; set; }

    /// <summary>pack_id del pedido; si el pedido no tiene pack, el id del pedido.</summary>
    [MaxLength(40)]
    public string PackId { get; set; } = string.Empty;

    public int? SalesOrderId { get; set; }

    [MaxLength(80)]
    public string ExternalId { get; set; } = string.Empty;

    public bool FromSeller { get; set; }

    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    public DateTime SentAt { get; set; }

    /// <summary>Estado de moderación de Mercado Libre (available, moderated, rejected…).</summary>
    [MaxLength(30)]
    public string? Status { get; set; }

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Reclamo, devolución o cancelación abierta por el comprador (post-purchase/v1/claims).</summary>
public sealed class OrderClaim
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int MarketplaceAccountId { get; set; }

    [MaxLength(40)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Pedido (resource_id) al que pertenece.</summary>
    [MaxLength(40)]
    public string? ResourceId { get; set; }

    public int? SalesOrderId { get; set; }

    /// <summary>mediations, return, cancel_purchase, cancel_sale, change…</summary>
    [MaxLength(40)]
    public string? Type { get; set; }

    /// <summary>claim (entre comprador y vendedor) o dispute (ya intervino Mercado Libre).</summary>
    [MaxLength(30)]
    public string? Stage { get; set; }

    /// <summary>opened o closed.</summary>
    [MaxLength(30)]
    public string Status { get; set; } = "opened";

    [MaxLength(40)]
    public string? ReasonId { get; set; }

    /// <summary>Lo que el vendedor tiene que hacer ahora (send_message_to_complainant, refund, allow_return…).</summary>
    [MaxLength(200)]
    public string? PendingActions { get; set; }

    /// <summary>Fecha límite de la acción obligatoria del vendedor.</summary>
    public DateTime? DueDate { get; set; }

    [MaxLength(80)]
    public string? Resolution { get; set; }

    public DateTime DateCreated { get; set; }
    public DateTime LastUpdated { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cuándo la pieza devuelta regresó al inventario (con su condición).</summary>
    public DateTime? ReturnRestockedAt { get; set; }

    [MaxLength(30)]
    public string? ReturnCondition { get; set; }

    public bool IsOpen => Status == "opened";

    /// <summary>Motivo en palabras simples, por el prefijo del reason_id de Mercado Libre.</summary>
    public string ReasonLabel => (ReasonId ?? string.Empty).ToUpperInvariant() switch
    {
        var r when r.StartsWith("PNR") => "No le llegó el producto",
        var r when r.StartsWith("PDD") => "Producto diferente o con defecto",
        var r when r.StartsWith("CS") => "Quiere cancelar la compra",
        _ => Type switch
        {
            "return" => "Devolución",
            "cancel_purchase" or "cancel_sale" => "Cancelación",
            "change" => "Cambio de producto",
            _ => "Reclamo"
        }
    };
}
