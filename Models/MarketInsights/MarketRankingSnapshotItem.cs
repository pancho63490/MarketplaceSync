namespace MarketplaceSync.Web.Models.MarketInsights;

public sealed class MarketRankingSnapshotItem
{
    public long Id { get; set; }
    public long MarketRankingSnapshotId { get; set; }
    public MarketRankingSnapshot MarketRankingSnapshot { get; set; } = null!;
    public int Position { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? ImageUrl { get; set; }
    public decimal? Price { get; set; }
    public string? CurrencyId { get; set; }
    public string? Permalink { get; set; }
    public int OpportunityScore { get; set; }
}
