using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;

namespace MarketplaceSync.Web.ViewModels.Publications;

public sealed class PublicationEditViewModel
{
    public required ItemDetails Item { get; init; }
    /// <summary>Precio automático de Mercado Libre (null si no se pudo leer).</summary>
    public MarketplaceSync.Services.MercadoLibre.AutoPricePanel? AutoPrice { get; init; }
    public decimal? LandedCostMxn { get; init; }
    public CrossBorderProfitEstimate? Profit { get; init; }

    /// <summary>Vehículos compatibles guardados en el producto (sugeridos o capturados).</summary>
    public List<MarketplaceSync.Services.MercadoLibre.VehicleFitment> Compatibilities { get; init; } = new();

    public int? CompatibilityCount { get; init; }
    public DateTime? CompatibilitySentAt { get; init; }

    // ── Promociones ──

    /// <summary>La publicación como está en la app (con la promoción activa, si tiene).</summary>
    public MarketplaceSync.Web.Models.Marketplace.MarketplacePublication? Publication { get; init; }

    /// <summary>Campañas de Mercado Libre a las que puede entrar (con la ganancia a ese precio).</summary>
    public List<CampaignRow> CampaignOffers { get; init; } = new();

    public List<DiscountOption> DiscountOptions { get; init; } = new();

    /// <summary>Ganancia por pieza con el precio de la promoción activa.</summary>
    public decimal? PromotionProfit { get; init; }

    /// <summary>No se pudo leer de Mercado Libre en qué promociones está.</summary>
    public bool PromotionsUnavailable { get; init; }
}
