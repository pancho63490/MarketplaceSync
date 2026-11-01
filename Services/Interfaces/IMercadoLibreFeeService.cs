namespace MarketplaceSync.Services.Interfaces;

public interface IMercadoLibreFeeService
{
    Task<MercadoLibreFeeEstimate> EstimateAsync(
        string accessToken,
        string categoryId,
        decimal price,
        string currencyId,
        string listingTypeId,
        CancellationToken cancellationToken = default);
}

public sealed record MercadoLibreFeeEstimate(
    decimal SaleFeeAmount,
    decimal PercentageFee,
    decimal FixedFee,
    string CurrencyId,
    string ListingTypeId);
