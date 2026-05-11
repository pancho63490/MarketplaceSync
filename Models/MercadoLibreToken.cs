using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models
{
    public class MercadoLibreToken
    {
        public int Id { get; set; }

        [MaxLength(100)]
        public string? UserId { get; set; }

        [Required]
        public string AccessToken { get; set; } = string.Empty;

        public string? RefreshToken { get; set; }

        [MaxLength(50)]
        public string? TokenType { get; set; }

       public string? Scope { get; set; }

        public int? ExpiresIn { get; set; }

        public DateTime? ExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}