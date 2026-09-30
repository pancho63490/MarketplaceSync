using System.ComponentModel.DataAnnotations;
using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.Models
{
    public class MercadoLibreToken
    {
        public int Id { get; set; }

        public Guid OrganizationId { get; set; }

        public Organization Organization { get; set; } = null!;

        public int? MarketplaceAccountId { get; set; }

        public MarketplaceAccount? MarketplaceAccount { get; set; }

        [MaxLength(450)]
        public string? ConnectedByUserId { get; set; }

        // Usuario interno de tu app
        [MaxLength(200)]
        public string? AppUserName { get; set; }

        // Usuario real de Mercado Libre
        [MaxLength(200)]
        public string? UserId { get; set; }

        [MaxLength(300)]
        public string? Nickname { get; set; }

        [Required]
        public string AccessToken { get; set; } = string.Empty;

        public string? RefreshToken { get; set; }

        [MaxLength(100)]
        public string? TokenType { get; set; }

        public string? Scope { get; set; }

        public int ExpiresIn { get; set; }

        public DateTime? ExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;
    }
}
