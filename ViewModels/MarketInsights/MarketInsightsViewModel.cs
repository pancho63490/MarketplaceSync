using MarketplaceSync.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MarketplaceSync.Web.ViewModels.MarketInsights;

public sealed class MarketInsightsViewModel
{
    public string? CategoryId { get; init; }
    public int? MarketplaceAccountId { get; init; }
    public bool CategorySelectedAutomatically { get; init; }
    public string? SearchText { get; init; }
    public IReadOnlyList<CategorySearchSuggestion> CategorySuggestions { get; init; }
        = Array.Empty<CategorySearchSuggestion>();
    public IReadOnlyList<SelectListItem> Accounts { get; init; } = Array.Empty<SelectListItem>();
    public IReadOnlyList<SavedMarketplaceCategory> SavedCategories { get; init; }
        = Array.Empty<SavedMarketplaceCategory>();
    public MercadoLibreBestSellerResult? Ranking { get; init; }
    public MarketOpportunityAnalysis? OpportunityAnalysis { get; init; }
    public MarketRankingHistory? History { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record SavedMarketplaceCategory(string CategoryId, int ProductCount, string? Name = null);

public sealed record CategorySearchSuggestion(string CategoryId, string Name);
