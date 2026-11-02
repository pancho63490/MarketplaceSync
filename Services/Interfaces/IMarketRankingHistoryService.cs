namespace MarketplaceSync.Services.Interfaces;

public interface IMarketRankingHistoryService
{
    Task<bool> CaptureAsync(
        Guid organizationId,
        int marketplaceAccountId,
        MercadoLibreBestSellerResult ranking,
        MarketOpportunityAnalysis analysis,
        CancellationToken cancellationToken = default);

    Task<int> CaptureAllAsync(CancellationToken cancellationToken = default);

    Task<MarketRankingHistory> GetHistoryAsync(
        Guid organizationId,
        string categoryId,
        int days,
        CancellationToken cancellationToken = default);
}

public sealed record MarketRankingHistory(
    int Days,
    int SnapshotCount,
    DateTime? LatestCapturedAt,
    DateTime? PreviousCapturedAt,
    IReadOnlyList<MarketRankingTrendItem> Trends,
    IReadOnlyList<MarketRankingSeries> Series);

public sealed record MarketRankingTrendItem(
    string ExternalId,
    string Name,
    string? ImageUrl,
    int? CurrentPosition,
    int? PreviousPosition,
    int? PositionChange,
    decimal? CurrentPrice,
    decimal? PreviousPrice,
    int? OpportunityScore,
    bool IsNew,
    bool HasExited);

public sealed record MarketRankingSeries(
    string ExternalId,
    string Name,
    IReadOnlyList<MarketRankingSeriesPoint> Points);

public sealed record MarketRankingSeriesPoint(DateOnly Date, int? Position);
