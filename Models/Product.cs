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

        /// <summary>Código de barras (UPC/EAN/GTIN) que trae el anuncio de origen, ya validado. Nunca se inventa.</summary>
        [MaxLength(14)]
        public string? SourceGtin { get; set; }

        /// <summary>Cuándo se buscó el código en el anuncio de origen (aunque no lo trajera).</summary>
        public DateTime? SourceGtinCheckedAt { get; set; }

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

        /// <summary>Foto original antes de ponerle fondo blanco (para deshacer).</summary>
        [MaxLength(1000)]
        public string? OriginalImageUrl { get; set; }

        /// <summary>Revisión de la portada: ok, fill, plain, busy, small, error (ver PhotoStudioService).</summary>
        [MaxLength(20)]
        public string? PhotoCheck { get; set; }

        public int? PhotoWidth { get; set; }
        public int? PhotoHeight { get; set; }
        public DateTime? PhotoCheckedAt { get; set; }

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

        public ICollection<ProductVariant> Variants { get; set; }
            = new List<ProductVariant>();
    }
    
}
