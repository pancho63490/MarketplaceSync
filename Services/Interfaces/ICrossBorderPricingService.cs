using MarketplaceSync.Web.Models.CrossBorder;

namespace MarketplaceSync.Services.Interfaces;

public sealed record CrossBorderPricingBreakdown(
    decimal SourcePriceUsd,
    decimal ExchangeRate,
    decimal ProductCostMxn,
    decimal ImportTaxesMxn,
    decimal LandedCostJuarezMxn,
    decimal EstimatedShippingMxn,
    decimal TargetNetProfitMxn,
    decimal MarketplaceFeePercent,
    decimal MarketplaceFeeMxn,
    decimal TaxWithholdingPercent,
    decimal TaxWithholdingMxn,
    decimal SuggestedListingPriceMxn,
    decimal EffectiveNetProfitMxn,
    decimal EffectiveNetMarginPercent,
    bool IsProfitable);

/// <summary>
/// Utilidad estimada al vender a un precio dado (por ejemplo, el precio actual del mercado).
/// </summary>
public sealed record CrossBorderProfitEstimate(
    decimal SourcePriceUsd,
    decimal ExchangeRate,
    decimal LandedCostJuarezMxn,
    decimal ListingPriceMxn,
    decimal ShippingOrFixedFeeMxn,
    decimal MarketplaceFeeMxn,
    decimal TaxWithholdingMxn,
    decimal NetProfitMxn,
    decimal NetMarginPercent,
    bool IsProfitable);

public interface ICrossBorderPricingService
{
    Task<CrossBorderSettings> GetOrCreateSettingsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<CrossBorderSettings> SaveSettingsAsync(
        CrossBorderSettings settings,
        CancellationToken cancellationToken = default);

    CrossBorderPricingBreakdown CalculatePricing(
        decimal sourcePriceUsd,
        CrossBorderSettings settings,
        decimal? customMarketplaceFeePercent = null);

    CrossBorderPricingBreakdown CalculatePricingFromMxn(
        decimal productCostMxn,
        CrossBorderSettings settings,
        decimal? customMarketplaceFeePercent = null);

    /// <summary>
    /// Calcula la ganancia neta si se compra a <paramref name="sourcePriceUsd"/> (producto + envío en EE.UU.)
    /// y se vende en Mercado Libre a <paramref name="listingPriceMxn"/>.
    /// </summary>
    CrossBorderProfitEstimate CalculateProfitAtPrice(
        decimal sourcePriceUsd,
        decimal listingPriceMxn,
        CrossBorderSettings settings,
        decimal? customMarketplaceFeePercent = null);

    /// <summary>
    /// Ganancia por pieza cuando ya conoces el costo puesto en Juárez (MXN) y, si la tienes,
    /// la comisión real de Mercado Libre para ese precio.
    /// </summary>
    /// <summary>Precio para ganar lo de Costos de cruce partiendo del costo puesto en Juárez.</summary>
    decimal SuggestPriceFromLandedCost(decimal landedCostMxn, CrossBorderSettings settings, decimal? feePercent = null);

    CrossBorderProfitEstimate CalculateProfitFromLandedCost(
        decimal landedCostMxn,
        decimal listingPriceMxn,
        CrossBorderSettings settings,
        decimal? actualSaleFeeMxn = null);

    /// <summary>Costo puesto en Juárez (MXN) a partir del costo de compra y su moneda.</summary>
    decimal LandedCostMxn(decimal sourceCost, string? currency, CrossBorderSettings settings);

    /// <summary>
    /// Para piezas de menos de $299: compara venderla sola (cargo fijo de Mercado Libre) contra un kit que pase de $299
    /// (envío gratis que paga el vendedor). Null si el precio ya es de $299 o más.
    /// </summary>
    KitSuggestion? SuggestKit(decimal landedCostMxn, decimal unitPriceMxn, CrossBorderSettings settings);
}

/// <summary>Comparación pieza sola vs. kit para precios bajo $299.</summary>
public sealed record KitSuggestion(
    int Quantity,
    decimal KitPriceMxn,
    decimal UnitProfitMxn,
    decimal KitProfitMxn,
    decimal KitProfitPerUnitMxn,
    bool KitIsBetter);
