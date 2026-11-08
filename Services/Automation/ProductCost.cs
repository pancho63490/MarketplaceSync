using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.CrossBorder;

namespace MarketplaceSync.Services.Automation;

/// <summary>
/// Costo puesto en Juárez de un producto: el real del último viaje a El Paso si existe; si no, el estimado
/// con Costos de cruce (precio de compra × tipo de cambio + % de cruce + manejo).
/// </summary>
public static class ProductCost
{
    public static bool HasEstimate(decimal? sourcePrice, string? sourceMarketplace) =>
        sourcePrice is > 0 && sourceMarketplace is not ("MercadoLibre" or "MercadoLibreInsights");

    public static bool Known(Product product) =>
        product.RealLandedCostMxn is > 0 || HasEstimate(product.SourcePrice, product.SourceMarketplace);

    public static decimal? Landed(Product product, CrossBorderSettings settings, ICrossBorderPricingService pricing) =>
        Landed(product.RealLandedCostMxn, product.SourcePrice, product.SourceCurrency, product.SourceMarketplace, settings, pricing);

    public static decimal? Landed(decimal? real, decimal? sourcePrice, string? sourceCurrency, string? sourceMarketplace,
        CrossBorderSettings settings, ICrossBorderPricingService pricing) =>
        real is > 0 ? real
        : HasEstimate(sourcePrice, sourceMarketplace) ? pricing.LandedCostMxn(sourcePrice!.Value, sourceCurrency, settings)
        : null;
}
