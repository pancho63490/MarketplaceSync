using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.ViewModels
{
    public class CreateProductFromUrlViewModel
    {
        [Required(ErrorMessage = "Ingresa el link del producto.")]
        [Url(ErrorMessage = "Ingresa una URL válida.")]
        [Display(Name = "URL del producto")]
        public string SourceUrl { get; set; } = string.Empty;

        /// <summary>Precio que ves en la tienda (USD). Walmart, RockAuto y a veces Amazon no dejan leerlo desde el servidor.</summary>
        [Range(0.01, 100000, ErrorMessage = "Escribe el precio en dólares, por ejemplo 38.50.")]
        [Display(Name = "Precio en la tienda (USD)")]
        public decimal? ManualPriceUsd { get; set; }
    }
}