using MarketplaceSync.Services.PostSale;
using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.PostSale;

namespace MarketplaceSync.Web.ViewModels.Orders;

public sealed class OrderDetailsViewModel
{
    public required SalesOrder Order { get; init; }
    /// <summary>Mensajes rápidos ya con el nombre del comprador y el producto (título, texto).</summary>
    public IReadOnlyList<(int Id, string Title, string Text)> QuickMessages { get; init; } = Array.Empty<(int, string, string)>();
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = Array.Empty<ConversationMessage>();
    public string? MessagesError { get; init; }
    public IReadOnlyList<OrderClaim> Claims { get; init; } = Array.Empty<OrderClaim>();
    public BlockedBuyer? Blocked { get; init; }
    public int BuyerOrders { get; init; }

    /// <summary>Factura de la venta (null si no está pagada).</summary>
    public MarketplaceSync.Services.Invoicing.InvoiceDraft? Invoice { get; init; }

    public List<string> InvoiceMissing { get; init; } = new();
}

/// <summary>Panel “Bloquear comprador” (en el pedido y en el reclamo).</summary>
public sealed record BlockBuyerPartial(int OrderId, string? Nickname, BlockedBuyer? Blocked, int Orders, long? ClaimId);
