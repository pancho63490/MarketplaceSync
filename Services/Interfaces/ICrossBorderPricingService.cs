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
}
