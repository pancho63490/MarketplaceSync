using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.CrossBorder;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.CrossBorder;

public sealed class CrossBorderPricingService : ICrossBorderPricingService
{
    private readonly AppDbContext _dbContext;

    public CrossBorderPricingService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CrossBorderSettings> GetOrCreateSettingsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var settings = await _dbContext.CrossBorderSettings
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);

        if (settings is not null)
            return settings;

        settings = new CrossBorderSettings
        {
            OrganizationId = organizationId,
            UsdToMxnRate = 20.0000m,
            ImportFeePercent = 15.00m,
            AdditionalHandlingFeeUsd = 0.00m,
            TargetProfitMarginPercent = 25.00m,
            MinProfitAmountMxn = 150.00m,
            EstimatedShippingMxn = 110.00m,
            DefaultMarketplaceFeePercent = 14.50m,
            PlatformTaxWithholdingPercent = 9.00m,
            AutoPublishEnabled = false,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.CrossBorderSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<CrossBorderSettings> SaveSettingsAsync(
        CrossBorderSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.UpdatedAt = DateTime.UtcNow;
        if (settings.Id == 0)
        {
            _dbContext.CrossBorderSettings.Add(settings);
        }
        else
        {
            _dbContext.CrossBorderSettings.Update(settings);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public CrossBorderPricingBreakdown CalculatePricing(
        decimal sourcePriceUsd,
        CrossBorderSettings settings,
        decimal? customMarketplaceFeePercent = null)
    {
        var exchangeRate = settings.UsdToMxnRate > 0 ? settings.UsdToMxnRate : 20.0000m;
        var productCostMxn = Math.Round(sourcePriceUsd * exchangeRate, 2);
        return CalculateInternal(
            sourcePriceUsd,
            exchangeRate,
            productCostMxn,
            settings,
            customMarketplaceFeePercent);
    }

    public CrossBorderPricingBreakdown CalculatePricingFromMxn(
        decimal productCostMxn,
        CrossBorderSettings settings,
        decimal? customMarketplaceFeePercent = null)
    {
        var exchangeRate = settings.UsdToMxnRate > 0 ? settings.UsdToMxnRate : 20.0000m;
        var sourcePriceUsd = exchangeRate > 0 ? Math.Round(productCostMxn / exchangeRate, 2) : 0m;
        return CalculateInternal(
            sourcePriceUsd,
            exchangeRate,
            productCostMxn,
            settings,
            customMarketplaceFeePercent);
    }

    public CrossBorderProfitEstimate CalculateProfitAtPrice(
        decimal sourcePriceUsd,
        decimal listingPriceMxn,
        CrossBorderSettings settings,
        decimal? customMarketplaceFeePercent = null)
    {
        const decimal freeShippingThresholdMxn = 299m;
        const decimal fixedFeeBelowThresholdMxn = 25m;

        var exchangeRate = settings.UsdToMxnRate > 0 ? settings.UsdToMxnRate : 20.0000m;
        var productCostMxn = Math.Round(Math.Max(0m, sourcePriceUsd) * exchangeRate, 2);
        var importFeeRate = Math.Max(0m, settings.ImportFeePercent / 100m);
        var importTaxesMxn = Math.Round(
            (productCostMxn * importFeeRate) + (settings.AdditionalHandlingFeeUsd * exchangeRate),
            2);
        var landedCostJuarezMxn = productCostMxn + importTaxesMxn;

        var meliFeePercent = customMarketplaceFeePercent ?? settings.DefaultMarketplaceFeePercent;
        var marketplaceFeeMxn = Math.Round(listingPriceMxn * (meliFeePercent / 100m), 2);
        var taxWithholdingMxn = Math.Round(listingPriceMxn * (settings.PlatformTaxWithholdingPercent / 100m), 2);
        var shippingOrFixedFeeMxn = listingPriceMxn >= freeShippingThresholdMxn
            ? settings.EstimatedShippingMxn
            : fixedFeeBelowThresholdMxn;

        var netProfitMxn = Math.Round(
            listingPriceMxn - landedCostJuarezMxn - shippingOrFixedFeeMxn - marketplaceFeeMxn - taxWithholdingMxn,
            2);
        var netMarginPercent = listingPriceMxn > 0
            ? Math.Round((netProfitMxn / listingPriceMxn) * 100m, 2)
            : 0m;

        return new CrossBorderProfitEstimate(
            sourcePriceUsd,
            exchangeRate,
            landedCostJuarezMxn,
            listingPriceMxn,
            shippingOrFixedFeeMxn,
            marketplaceFeeMxn,
            taxWithholdingMxn,
            netProfitMxn,
            netMarginPercent,
            netProfitMxn >= settings.MinProfitAmountMxn);
    }

    private static CrossBorderPricingBreakdown CalculateInternal(
        decimal sourcePriceUsd,
        decimal exchangeRate,
        decimal productCostMxn,
        CrossBorderSettings settings,
        decimal? customMarketplaceFeePercent)
    {
        var importFeeRate = Math.Max(0m, settings.ImportFeePercent / 100m);
        var importTaxesMxn = Math.Round(
            (productCostMxn * importFeeRate) + (settings.AdditionalHandlingFeeUsd * exchangeRate),
            2);
        var landedCostJuarezMxn = productCostMxn + importTaxesMxn;

        var targetMarginAmount = Math.Round(
            landedCostJuarezMxn * (Math.Max(0m, settings.TargetProfitMarginPercent) / 100m),
            2);
        var targetNetProfitMxn = Math.Max(targetMarginAmount, settings.MinProfitAmountMxn);

        var meliFeePercent = customMarketplaceFeePercent ?? settings.DefaultMarketplaceFeePercent;
        var taxWithholdingPercent = settings.PlatformTaxWithholdingPercent;
        var totalDeductionPercent = meliFeePercent + taxWithholdingPercent;
        var deductionRate = Math.Clamp(totalDeductionPercent / 100m, 0.05m, 0.85m);

        var shippingMxn = settings.EstimatedShippingMxn;
        var rawPrice = (landedCostJuarezMxn + shippingMxn + targetNetProfitMxn) / (1m - deductionRate);

        // Si el precio calculado queda abajo de 299 MXN, en MeLi el envío lo paga el comprador,
        // pero MeLi aplica un cargo fijo de aprox $25 MXN.
        if (rawPrice < 299m)
        {
            const decimal fixedFeeBelowThreshold = 25m;
            rawPrice = (landedCostJuarezMxn + fixedFeeBelowThreshold + targetNetProfitMxn) / (1m - deductionRate);
            shippingMxn = 0m;
        }

        var suggestedListingPriceMxn = Math.Ceiling(rawPrice);
        var marketplaceFeeMxn = Math.Round(suggestedListingPriceMxn * (meliFeePercent / 100m), 2);
        var taxWithholdingMxn = Math.Round(suggestedListingPriceMxn * (taxWithholdingPercent / 100m), 2);
        var actualShipping = suggestedListingPriceMxn >= 299m ? shippingMxn : 0m;

        var effectiveNetProfitMxn = Math.Round(
            suggestedListingPriceMxn - landedCostJuarezMxn - actualShipping - marketplaceFeeMxn - taxWithholdingMxn,
            2);
        var effectiveMarginPercent = suggestedListingPriceMxn > 0
            ? Math.Round((effectiveNetProfitMxn / suggestedListingPriceMxn) * 100m, 2)
            : 0m;

        var isProfitable = effectiveNetProfitMxn >= settings.MinProfitAmountMxn;

        return new CrossBorderPricingBreakdown(
            sourcePriceUsd,
            exchangeRate,
            productCostMxn,
            importTaxesMxn,
            landedCostJuarezMxn,
            actualShipping,
            targetNetProfitMxn,
            meliFeePercent,
            marketplaceFeeMxn,
            taxWithholdingPercent,
            taxWithholdingMxn,
            suggestedListingPriceMxn,
            effectiveNetProfitMxn,
            effectiveMarginPercent,
            isProfitable);
    }
}
