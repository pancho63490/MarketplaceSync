using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Marketplace;

public class MarketplaceAccount
{
    public int Id { get; set; }

    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [Required, MaxLength(50)]
    public string Marketplace { get; set; } = "MercadoLibre";

    [Required, MaxLength(200)]
    public string ExternalAccountId { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? DisplayName { get; set; }

    [Required, MaxLength(30)]
    public string Status { get; set; } = "Connected";

    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DisconnectedAt { get; set; }

    [MaxLength(450)]
    public string? ConnectedByUserId { get; set; }

    public ICollection<MercadoLibreToken> Tokens { get; set; }
        = new List<MercadoLibreToken>();
}
