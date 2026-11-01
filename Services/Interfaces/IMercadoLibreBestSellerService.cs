namespace MarketplaceSync.Services.Interfaces;

public interface IMercadoLibreBestSellerService
{
    Task<MercadoLibreBestSellerResult> GetTopAsync(
        string categoryId,
        string accessToken,
        int limit = 10,
        CancellationToken cancellationToken = default);
}

public sealed record MercadoLibreBestSellerResult(
    string CategoryId,
    string CategoryName,
    int? TotalActiveListings,
    DateTime RetrievedAt,
    IReadOnlyList<MercadoLibreBestSellerItem> Items);

public sealed record MercadoLibreBestSellerItem(
    int Position,
    string Id,
    string Type,
    string Name,
    string? Brand,
    string? ImageUrl,
    decimal? Price,
    string? CurrencyId,
    string? Permalink);
