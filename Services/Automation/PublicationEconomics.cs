using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.CrossBorder;
using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Services.Automation;

/// <summary>Cuánto gana una publicación a su precio actual y qué precio le haría ganar lo mínimo.</summary>
public sealed record PublicationMoney(decimal LandedCostMxn, CrossBorderProfitEstimate Profit, decimal SuggestedPriceMxn)
{
    /// <summary>No llega a la ganancia mínima y el sugerido es más alto que el actual.</summary>
    public bool NeedsRaise => !Profit.IsProfitable && SuggestedPriceMxn > Profit.ListingPriceMxn;
}

/// <summary>Mismo cálculo que Publicados (costo puesto en Juárez, comisión real si se conoce, retenciones y envío).</summary>
public sealed class PublicationEconomics
{
    private readonly ICrossBorderPricingService _pricing;

    public PublicationEconomics(ICrossBorderPricingService pricing) => _pricing = pricing;

    public static bool CostKnown(Product product) =>
        product.SourcePrice is > 0 && product.SourceMarketplace is not ("MercadoLibre" or "MercadoLibreInsights");

    public PublicationMoney? Calculate(MarketplacePublication publication, Product product, CrossBorderSettings settings, decimal? atPrice = null)
    {
        var price = atPrice ?? publication.Price;
        if (!CostKnown(product) || price is not > 0)
            return null;

        var landed = _pricing.LandedCostMxn(product.SourcePrice!.Value, product.SourceCurrency, settings);
        var realFee = atPrice is null && publication.SaleFeeForPrice == publication.Price ? publication.SaleFeeMxn : null;
        var profit = _pricing.CalculateProfitFromLandedCost(landed, price.Value, settings, realFee);

        decimal? feePercent = publication.SaleFeeMxn is > 0 && publication.SaleFeeForPrice is >= 299m
            ? Math.Round(publication.SaleFeeMxn.Value / publication.SaleFeeForPrice.Value * 100m, 2)
            : null;
        var suggested = _pricing.SuggestPriceFromLandedCost(landed, settings, feePercent);
        return new PublicationMoney(landed, profit, suggested);
    }
}
