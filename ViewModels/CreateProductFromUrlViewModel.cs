using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.ViewModels
{
    public class CreateProductFromUrlViewModel
    {
        [Required(ErrorMessage = "Ingresa el link del producto.")]
        [Url(ErrorMessage = "Ingresa una URL válida.")]
        [Display(Name = "URL del producto")]
        public string SourceUrl { get; set; } = string.Empty;
    }
}