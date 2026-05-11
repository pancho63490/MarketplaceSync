using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.ViewModels
{
    public class PublishToMercadoLibreRequest
    {
        public int ProductId { get; set; }

        [Required]
        [Display(Name = "Categoría Mercado Libre")]
        public string CategoryId { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Título")]
        public string Title { get; set; } = string.Empty;

        [Required]
        [Range(1, 999999)]
        [Display(Name = "Precio")]
        public decimal Price { get; set; }

        [Required]
        [Range(1, 99999)]
        [Display(Name = "Stock")]
        public int Stock { get; set; }

        [Required]
        [Display(Name = "Condición")]
        public string Condition { get; set; } = "new";

        [Required]
        [Display(Name = "Tipo de publicación")]
        public string ListingTypeId { get; set; } = "gold_special";

        [Required]
        [Display(Name = "Moneda")]
        public string CurrencyId { get; set; } = "MXN";

        [Display(Name = "Imagen")]
        public string? PictureUrl { get; set; }
    }
}
