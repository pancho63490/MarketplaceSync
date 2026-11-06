using MarketplaceSync.Services.PostSale;
using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.PostSale;

namespace MarketplaceSync.Web.ViewModels.Orders;

public sealed class OrderDetailsViewModel
{
    public required SalesOrder Order { get; init; }
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = Array.Empty<ConversationMessage>();
    public string? MessagesError { get; init; }
    public IReadOnlyList<OrderClaim> Claims { get; init; } = Array.Empty<OrderClaim>();
}
