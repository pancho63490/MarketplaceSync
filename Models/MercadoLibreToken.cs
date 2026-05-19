using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models
{
    public class MercadoLibreToken
    {
        public int Id { get; set; }

        public string? UserId { get; set; }

        public string? Nickname { get; set; }

        [Required]
        public string AccessToken { get; set; } = string.Empty;

        public string? RefreshToken { get; set; }

        public string? TokenType { get; set; }

        public int ExpiresIn { get; set; }

        public DateTime? ExpiresAt { get; set; }

        public string? Scope { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;
    }
}