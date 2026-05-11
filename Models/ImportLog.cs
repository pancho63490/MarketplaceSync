using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models
{
    public class ImportLog
    {
        public int Id { get; set; }

        public int? ProductId { get; set; }

        [MaxLength(1000)]
        public string? SourceUrl { get; set; }

        [MaxLength(50)]
        public string? Marketplace { get; set; }

        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = "INFO";

        public string? Message { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}