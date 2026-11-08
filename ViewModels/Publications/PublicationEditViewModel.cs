using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;

namespace MarketplaceSync.Web.ViewModels.Publications;

public sealed class PublicationEditViewModel
{
    public required ItemDetails Item { get; init; }
    public decimal? LandedCostMxn { get; init; }
    public CrossBorderProfitEstimate? Profit { get; init; }

    /// <summary>Vehículos compatibles guardados en el producto (sugeridos o capturados).</summary>
    public List<MarketplaceSync.Services.MercadoLibre.VehicleFitment> Compatibilities { get; init; } = new();

    public int? CompatibilityCount { get; init; }
    public DateTime? CompatibilitySentAt { get; init; }
}
