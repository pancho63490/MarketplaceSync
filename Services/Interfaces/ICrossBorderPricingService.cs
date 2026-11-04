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
    CrossBorderProfitEstimate CalculateProfitFromLandedCost(
        decimal landedCostMxn,
        decimal listingPriceMxn,
        CrossBorderSettings settings,
        decimal? actualSaleFeeMxn = null);

    /// <summary>Costo puesto en Juárez (MXN) a partir del costo de compra y su moneda.</summary>
    decimal LandedCostMxn(decimal sourceCost, string? currency, CrossBorderSettings settings);
}
