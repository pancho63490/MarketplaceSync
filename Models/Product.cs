using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models
{
    public class Product
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(1000)]
        public string SourceUrl { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string SourceMarketplace { get; set; } = string.Empty;

        [MaxLength(150)]
        public string? SourceProductId { get; set; }

        [MaxLength(500)]
        public string? Title { get; set; }

        public string? Description { get; set; }

        [MaxLength(200)]
        public string? Brand { get; set; }

        [MaxLength(200)]
        public string? Model { get; set; }

        public decimal? SourcePrice { get; set; }

        [MaxLength(10)]
        public string? SourceCurrency { get; set; }

        public int? SourceStock { get; set; }

        public bool IsAvailable { get; set; } = false;

        [MaxLength(100)]
        public string? MLCategoryId { get; set; }

        [MaxLength(100)]
        public string? MLItemId { get; set; }

        public decimal? MLPrice { get; set; }

        public int? MLStock { get; set; }

        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = "DRAFT";

        public string? ErrorMessage { get; set; }

        public DateTime? LastCheckedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        public List<ProductImage> Images { get; set; } = new();
        public string? MercadoLibreItemId { get; set; }

public string? MercadoLibreCategoryId { get; set; }

public decimal? MercadoLibrePrice { get; set; }

public int? MercadoLibreStock { get; set; }

public string? MercadoLibreCurrencyId { get; set; } = "MXN";

public string? MercadoLibreListingTypeId { get; set; } = "gold_special";

public string? MercadoLibreCondition { get; set; } = "new";

public string? MercadoLibreStatus { get; set; }

public string? MercadoLibrePermalink { get; set; }

public DateTime? MercadoLibrePublishedAt { get; set; }
    }
}