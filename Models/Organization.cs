using System.ComponentModel.DataAnnotations;
using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.Models;

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(180)]
    public string Slug { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<OrganizationMembership> Memberships { get; set; }
        = new List<OrganizationMembership>();

    public ICollection<Product> Products { get; set; }
        = new List<Product>();

    public ICollection<MercadoLibreToken> MercadoLibreTokens { get; set; }
        = new List<MercadoLibreToken>();

    public ICollection<MarketplaceAccount> MarketplaceAccounts { get; set; }
        = new List<MarketplaceAccount>();
}
