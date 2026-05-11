using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models
{
    public class ProductImage
    {
        public int Id { get; set; }

        public int ProductId { get; set; }

        [Required]
        [MaxLength(1000)]
        public string ImageUrl { get; set; } = string.Empty;

        public int Position { get; set; } = 1;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public Product? Product { get; set; }
    }
}