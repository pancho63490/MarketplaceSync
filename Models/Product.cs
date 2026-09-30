using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;
using MarketplaceSync.Web.Models.Marketplace;
namespace MarketplaceSync.Web.Models
{
    public class Product
    {
        public int Id { get; set; }

        // =========================
        // Organización propietaria y usuario que creó el producto
        // =========================

        public Guid OrganizationId { get; set; }

        public Organization Organization { get; set; } = null!;

        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }

        [ForeignKey(nameof(CreatedByUserId))]
        public IdentityUser? CreatedByUser { get; set; }

        // =========================
        // Fuente original: Amazon/eBay/etc.
        // =========================

        [Required]
        [MaxLength(1000)]
        public string SourceUrl { get; set; } = string.Empty;

        [MaxLength(100)]
        public string SourceMarketplace { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? SourceProductId { get; set; }

        [MaxLength(300)]
        public string? Title { get; set; }

        public string? Description { get; set; }

        public decimal? SourcePrice { get; set; }

        [MaxLength(20)]
        public string? SourceCurrency { get; set; }

        public int? SourceStock { get; set; }

        [MaxLength(100)]
        public string? SourceAvailabilityText { get; set; }

        [MaxLength(1000)]
        public string? ImageUrl { get; set; }

        [MaxLength(200)]
        public string? Brand { get; set; }

        [MaxLength(200)]
        public string? Model { get; set; }

        [MaxLength(100)]
        public string? SourceStatus { get; set; }

        public DateTime? LastSourceCheckAt { get; set; }

        // =========================
        // Estado interno de tu app
        // =========================

        [MaxLength(100)]
        public string Status { get; set; } = "Draft";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        // =========================
        // Control de errores
        // =========================

        public string? LastErrorMessage { get; set; }

        public DateTime? LastErrorAt { get; set; }

        public ICollection<MarketplacePublication> MarketplacePublications { get; set; }
            = new List<MarketplacePublication>();
    }
    
}
