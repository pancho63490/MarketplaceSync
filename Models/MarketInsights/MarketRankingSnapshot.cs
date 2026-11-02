using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.Models.MarketInsights;

public sealed class MarketRankingSnapshot
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public int? MarketplaceAccountId { get; set; }
    public MarketplaceAccount? MarketplaceAccount { get; set; }
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int? TotalActiveListings { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public ICollection<MarketRankingSnapshotItem> Items { get; set; }
        = new List<MarketRankingSnapshotItem>();
}
