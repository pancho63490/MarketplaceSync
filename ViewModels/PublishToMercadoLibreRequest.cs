using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.ViewModels
{
    public class PublishToMercadoLibreRequest
    {
        public int ProductId { get; set; }

        [Required(ErrorMessage = "El título es requerido.")]
        [MaxLength(60, ErrorMessage = "Mercado Libre permite máximo 60 caracteres en el título.")]
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Required(ErrorMessage = "El precio es requerido.")]
        [Range(1, 999999999, ErrorMessage = "El precio debe ser mayor a 0.")]
        public decimal Price { get; set; }

        [Required(ErrorMessage = "El stock es requerido.")]
        [Range(1, 999999, ErrorMessage = "El stock debe ser mayor a 0.")]
        public int Stock { get; set; } = 1;

        [Required(ErrorMessage = "La moneda es requerida.")]
        public string CurrencyId { get; set; } = "MXN";

        [Required(ErrorMessage = "La categoría es requerida.")]
        public string CategoryId { get; set; } = string.Empty;

        [Required(ErrorMessage = "La condición es requerida.")]
        public string Condition { get; set; } = "new";

        [Required(ErrorMessage = "El tipo de publicación es requerido.")]
        public string ListingTypeId { get; set; } = "gold_special";

        public string? ImageUrl { get; set; }

        public string? Brand { get; set; }

        public string? Model { get; set; }

        public string? ErrorMessage { get; set; }

        public List<MercadoLibreAttributeInput> Attributes { get; set; } = new();
    }

    public class MercadoLibreAttributeInput
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool Required { get; set; }

        public string? ValueType { get; set; }

        public string? ValueId { get; set; }

        public string? ValueName { get; set; }

        public List<MercadoLibreAttributeValueOption> Values { get; set; } = new();
    }

    public class MercadoLibreAttributeValueOption
    {
        public string? Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}