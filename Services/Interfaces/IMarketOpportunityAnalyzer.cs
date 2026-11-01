namespace MarketplaceSync.Services.Interfaces;

public interface IMarketOpportunityAnalyzer
{
    MarketOpportunityAnalysis Analyze(
        MercadoLibreBestSellerResult ranking,
        IReadOnlySet<string> existingItemIds);
}

public sealed record MarketOpportunityAnalysis(
    decimal? MedianPrice,
    int? TotalActiveListings,
    string CategoryCompetitionLevel,
    IReadOnlyList<MarketOpportunity> Opportunities);

public sealed record MarketOpportunity(
    MercadoLibreBestSellerItem Item,
    int Score,
    int DemandScore,
    int PriceScore,
    int CompetitionScore,
    string Level,
    string Recommendation,
    bool AlreadyInCatalog);
