namespace MarketplaceSync.Web.Models.Marketplace;

public sealed class MarketplaceAccountStatusViewModel
{
    public int Id { get; init; }
    public string Marketplace { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ExternalAccountId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime ConnectedAt { get; init; }
    public DateTime? DisconnectedAt { get; init; }
    public DateTime? TokenExpiresAt { get; init; }
    public DateTime? TokenUpdatedAt { get; init; }
    public bool HasActiveToken { get; init; }
}
