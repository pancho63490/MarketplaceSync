namespace MarketplaceSync.Services.Interfaces;

/// <summary>
/// Result of a single product source check for the price/stock monitor.
/// </summary>
public sealed record SourceCheckResult(
    int ProductId,
    bool StockChanged,
    bool PriceChanged,
    int? NewStock,
    decimal? NewSourcePrice,
    decimal? NewListingPriceMxn,
    string Outcome);

public interface ISourcePriceMonitorService
{
    /// <summary>
    /// Checks all active products that have a SourceUrl from eBay/Amazon/AliExpress.
    /// Updates stock/price in DB and syncs to Mercado Libre if needed.
    /// Designed to be called by Hangfire every 4 hours.
    /// </summary>
    Task MonitorAllProductsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a single product's source price/stock.
    /// </summary>
    Task<SourceCheckResult> CheckProductAsync(int productId, CancellationToken cancellationToken = default);
}
