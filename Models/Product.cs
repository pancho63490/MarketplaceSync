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

        private string _sourceUrl = string.Empty;
        private string? _imageUrl;
        private string? _originalImageUrl;

        /// <summary>Link de la tienda en su forma corta (sin rastreo); nunca pasa de 1,000 caracteres.</summary>
        [Required]
        [MaxLength(1000)]
        public string SourceUrl
        {
            get => _sourceUrl;
            set => _sourceUrl = MarketplaceSync.Web.Helpers.UrlCleaner.CleanSourceUrl(value);
        }

        [MaxLength(100)]
        public string SourceMarketplace { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? SourceProductId { get; set; }

        /// <summary>Código de barras (UPC/EAN/GTIN) que trae el anuncio de origen, ya validado. Nunca se inventa.</summary>
        [MaxLength(14)]
        public string? SourceGtin { get; set; }

        /// <summary>Cuándo se buscó el código en el anuncio de origen (aunque no lo trajera).</summary>
        public DateTime? SourceGtinCheckedAt { get; set; }

        /// <summary>
        /// Vehículos con los que es compatible la pieza (JSON de <c>VehicleFitment</c>): sugeridos por IA o capturados,
        /// validados contra el catálogo de Mercado Libre; se mandan a la publicación solo al confirmar.
        /// </summary>
        /// <summary>Clave de producto o servicio del SAT (8 dígitos) para facturar; vacía = la de Facturación.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(8)]
    public string? SatProductCode { get; set; }

    public string? CompatibilityJson { get; set; }

        /// <summary>Vehículos (versiones) que Mercado Libre tiene ligados a la publicación tras el último envío.</summary>
        public int? CompatibilityCount { get; set; }

        public DateTime? CompatibilitySentAt { get; set; }

        /// <summary>De dónde salió <see cref="SourceGtin"/>: eBay, Walmart o internet (UPCitemdb).</summary>
        [MaxLength(80)]
        public string? SourceGtinOrigin { get; set; }

        [MaxLength(300)]
        public string? Title { get; set; }

        public string? Description { get; set; }

        public decimal? SourcePrice { get; set; }


        /// <summary>Costo real por pieza puesto en Juárez del último viaje a El Paso (compra, envío, gasolina, cruce e impuestos repartidos).

        /// Cuando existe, la ganancia se calcula con este y no con el estimado de Costos de cruce.</summary>

        [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(12,2)")]

        public decimal? RealLandedCostMxn { get; set; }


        public DateTime? RealLandedCostAt { get; set; }


        public int? RealLandedTripId { get; set; }

        [MaxLength(20)]
        public string? SourceCurrency { get; set; }

        public int? SourceStock { get; set; }

        [MaxLength(100)]
        public string? SourceAvailabilityText { get; set; }

        [MaxLength(1000)]
        public string? ImageUrl
        {
            get => _imageUrl;
            set => _imageUrl = MarketplaceSync.Web.Helpers.UrlCleaner.Fit(value);
        }

        /// <summary>Foto original antes de ponerle fondo blanco (para deshacer).</summary>
        [MaxLength(1000)]
        public string? OriginalImageUrl
        {
            get => _originalImageUrl;
            set => _originalImageUrl = MarketplaceSync.Web.Helpers.UrlCleaner.Fit(value);
        }

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
