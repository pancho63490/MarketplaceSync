using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.ViewModels
{
    public class PublishToMercadoLibreRequestv2
    {
        public int ProductId { get; set; }

        [Required(ErrorMessage = "El título es requerido.")]
        [MaxLength(60, ErrorMessage = "El título no puede pasar de 60 caracteres.")]
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Required(ErrorMessage = "El precio es requerido.")]
        [Range(1, 999999, ErrorMessage = "El precio debe ser mayor a 0.")]
        public decimal Price { get; set; }

        [Required(ErrorMessage = "El stock es requerido.")]
        [Range(1, 999999, ErrorMessage = "El stock debe ser mayor a 0.")]
        public int Stock { get; set; }

        [Required(ErrorMessage = "La moneda es requerida.")]
        public string CurrencyId { get; set; } = "MXN";

        [Required(ErrorMessage = "La categoría es requerida.")]
        public string CategoryId { get; set; } = string.Empty;

        public string Condition { get; set; } = "new";

        public string ListingTypeId { get; set; } = "gold_special";

        public string? ImageUrl { get; set; }

        public string? Brand { get; set; }

        public string? Model { get; set; }

        public List<MercadoLibreAttributeRequest> Attributes { get; set; } = new();

        public string? MercadoLibreResponseJson { get; set; }

        public string? ErrorMessage { get; set; }

        public string? SuccessMessage { get; set; }
    }

    public class MercadoLibreAttributeRequest
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? ValueName { get; set; }

        public bool Required { get; set; }
    }
}