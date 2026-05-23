using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MarketplaceSync.Web.Models.Marketplace
{
    public class MarketplacePublication
    {
        [Key]
        public int Id { get; set; }

        // =====================================
        // RELATIONSHIPS
        // =====================================

        public int ProductId { get; set; }

        [ForeignKey(nameof(ProductId))]
        public Product Product { get; set; }

        // =====================================
        // MARKETPLACE
        // =====================================

        [MaxLength(50)]
        public string Marketplace { get; set; } = "MercadoLibre";

        // =====================================
        // EXTERNAL
        // =====================================

        [MaxLength(200)]
        public string? ExternalItemId { get; set; }

        [MaxLength(1000)]
        public string? Permalink { get; set; }

        // =====================================
        // PUBLICATION DATA
        // =====================================

        [Column(TypeName = "numeric(18,2)")]
        public decimal? Price { get; set; }

        public int? Stock { get; set; }

        [MaxLength(20)]
        public string? CurrencyId { get; set; }

        [MaxLength(100)]
        public string? CategoryId { get; set; }

        [MaxLength(50)]
        public string? ListingTypeId { get; set; }

        [MaxLength(50)]
        public string? Condition { get; set; }

        // =====================================
        // STATUS
        // =====================================

        [MaxLength(100)]
        public string? Status { get; set; }

        public bool IsPublished { get; set; }

        // =====================================
        // DATES
        // =====================================

        public DateTime? PublishedAt { get; set; }

        public DateTime CreatedAt { get; set; } =
            DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } =
            DateTime.UtcNow;
    }
}