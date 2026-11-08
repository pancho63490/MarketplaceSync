using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MarketplaceSync.Web.Models.Automation;

/// <summary>
/// Algo que la app quiere hacer en Mercado Libre y espera que el vendedor confirme con un botón en Telegram
/// (lo propone el asistente o la revisión de precios). Nunca se ejecuta sin confirmación, salvo el modo automático de precios.
/// </summary>
public sealed class BotAction
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Ver <see cref="BotActionKinds"/>.</summary>
    [MaxLength(20)]
    public string Kind { get; set; } = string.Empty;

    public int? PublicationId { get; set; }

    [Column(TypeName = "numeric(12,2)")]
    public decimal? Value { get; set; }

    /// <summary>Lo que se le muestra al vendedor: "Subir Faro Tacoma de $3,499 a $3,890".</summary>
    [MaxLength(400)]
    public string Description { get; set; } = string.Empty;

    /// <summary>assistant o repricing.</summary>
    [MaxLength(20)]
    public string Source { get; set; } = string.Empty;

    /// <summary>pending, done, cancelled, failed o expired.</summary>
    [MaxLength(12)]
    public string Status { get; set; } = BotActionStatus.Pending;

    [MaxLength(500)]
    public string? Result { get; set; }

    /// <summary>Datos extra en JSON (días del descuento; campaña y publicaciones al entrar a una promoción).</summary>
    [MaxLength(4000)]
    public string? Payload { get; set; }

    public long? TelegramMessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
}

public static class BotActionKinds
{
    public const string Price = "price";
    public const string Pause = "pause";
    public const string Activate = "activate";

    /// <summary>Mandar a Mercado Libre las compatibilidades guardadas en el producto.</summary>
    public const string Compatibilities = "compat";

    /// <summary>Descuento individual (Value = %, Payload {"days":7}).</summary>
    public const string Discount = "discount";

    /// <summary>Quitar la promoción en la que está la publicación.</summary>
    public const string DiscountRemove = "undiscount";

    /// <summary>Entrar a una campaña de Mercado Libre con varias publicaciones (Payload con campaña y precios).</summary>
    public const string PromotionJoin = "promo";

    /// <summary>Timbrar la factura de una venta con los datos que dio el comprador (Payload {"orderId":1}).</summary>
    public const string Invoice = "invoice";

    /// <summary>Timbrar la factura global de un mes (Payload {"year":2026,"month":10}).</summary>
    public const string GlobalInvoice = "global_invoice";

    /// <summary>Pausar un anuncio de Mercado Ads (Payload {"accountId":1,"itemId":"MLM1"}).</summary>
    public const string AdPause = "ad_pause";
}

public static class BotActionStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

/// <summary>Conversación reciente con el asistente de Telegram (para que entienda "¿y ayer?").</summary>
public sealed class AssistantTurn
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }

    [MaxLength(10)]
    public string Role { get; set; } = "user";

    [MaxLength(4000)]
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Lista de compra semanal que se aprueba en Telegram.</summary>
public sealed class PurchaseList
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>proposed, approved, skipped o received.</summary>
    [MaxLength(12)]
    public string Status { get; set; } = "proposed";

    [Column(TypeName = "numeric(12,2)")]
    public decimal TotalUsd { get; set; }

    public long? TelegramMessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ReceivedAt { get; set; }

    public List<PurchaseListItem> Items { get; set; } = new();
}

public sealed class PurchaseListItem
{
    public int Id { get; set; }
    public int PurchaseListId { get; set; }
    public PurchaseList PurchaseList { get; set; } = null!;

    /// <summary>restock (producto tuyo que se está acabando) o new (recomendado por Qué comprar).</summary>
    [MaxLength(10)]
    public string Kind { get; set; } = "restock";

    public int? ProductVariantId { get; set; }
    public long? CandidateId { get; set; }

    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Sku { get; set; }

    public int Quantity { get; set; }

    [Column(TypeName = "numeric(12,2)")]
    public decimal? UnitCostUsd { get; set; }

    [MaxLength(1000)]
    public string? Url { get; set; }

    /// <summary>Por qué se compra: "vendió 6 en 30 días, quedan 1".</summary>
    [MaxLength(200)]
    public string? Reason { get; set; }
}
