using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.PostSale;

/// <summary>Comprador bloqueado en Mercado Libre (no puede comprarte ni preguntarte).</summary>
public sealed class BlockedBuyer
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int MarketplaceAccountId { get; set; }

    [MaxLength(30)]
    public string BuyerId { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? Nickname { get; set; }

    [MaxLength(300)]
    public string? Reason { get; set; }

    /// <summary>Bloqueado para comprar (order_blacklist).</summary>
    public bool BlocksOrders { get; set; } = true;

    /// <summary>Bloqueado para preguntar (questions_blacklist).</summary>
    public bool BlocksQuestions { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
