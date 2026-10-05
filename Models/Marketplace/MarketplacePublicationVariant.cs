using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Marketplace;

public class MarketplacePublicationVariant
{
    public int Id { get; set; }
    public int MarketplacePublicationId { get; set; }
    public MarketplacePublication MarketplacePublication { get; set; } = null!;
    public int ProductVariantId { get; set; }
    public ProductVariant ProductVariant { get; set; } = null!;

    [MaxLength(100)]
    public string? ExternalVariationId { get; set; }
}
