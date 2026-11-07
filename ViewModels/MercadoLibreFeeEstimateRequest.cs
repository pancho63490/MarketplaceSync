using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.ViewModels;

public sealed class MercadoLibreFeeEstimateRequest
{
    [Required]
    public int? MarketplaceAccountId { get; set; }

    [Required, RegularExpression("^MLM\\d+$")]
    public string CategoryId { get; set; } = string.Empty;

    [Range(1, 999999999)]
    public decimal Price { get; set; }

    [Required]
    public string CurrencyId { get; set; } = "MXN";

    [Required]
    public string ListingTypeId { get; set; } = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DefaultListingTypeId;
}
