using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models
{
    public class Product
    {
        public int Id { get; set; }

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

        // =========================
        // Datos de publicación en Mercado Libre
        // =========================

        [MaxLength(100)]
        public string? MercadoLibreItemId { get; set; }

        [MaxLength(100)]
        public string? MercadoLibreCategoryId { get; set; }

        public decimal? MercadoLibrePrice { get; set; }

        public int? MercadoLibreStock { get; set; }

        [MaxLength(20)]
        public string? MercadoLibreCurrencyId { get; set; } = "MXN";

        [MaxLength(100)]
        public string? MercadoLibreListingTypeId { get; set; } = "gold_special";

        [MaxLength(50)]
        public string? MercadoLibreCondition { get; set; } = "new";

        [MaxLength(100)]
        public string? MercadoLibreStatus { get; set; }

        [MaxLength(1000)]
        public string? MercadoLibrePermalink { get; set; }

        public DateTime? MercadoLibrePublishedAt { get; set; }
        
        
    }
}