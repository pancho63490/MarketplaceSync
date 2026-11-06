using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;

namespace MarketplaceSync.Web.ViewModels.Publications;

public sealed class PublicationEditViewModel
{
    public required ItemDetails Item { get; init; }
    public decimal? LandedCostMxn { get; init; }
    public CrossBorderProfitEstimate? Profit { get; init; }
}
