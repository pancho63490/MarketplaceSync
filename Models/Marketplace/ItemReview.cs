using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Marketplace;

/// <summary>Opinión de un comprador sobre una publicación (de <c>/reviews/item/{id}</c>).</summary>
public class ItemReview
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int PublicationId { get; set; }
    public MarketplacePublication? Publication { get; set; }

    /// <summary>Id de la opinión en Mercado Libre.</summary>
    [MaxLength(40)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Estrellas, de 1 a 5.</summary>
    public int Rate { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(2000)]
    public string? Content { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? BuyingDate { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
}
