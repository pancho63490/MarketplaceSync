namespace MarketplaceSync.Services.Interfaces;

public sealed record PurchaseRadarRefreshResult(
    int CandidatesEvaluated,
    int SourcesSearched,
    int SourcesMatched,
    int Profitable,
    string? Warning);

public sealed record RestockSuggestion(
    int ProductVariantId,
    int ProductId,
    string Sku,
    string Title,
    string? ImageUrl,
    int UnitsSold30Days,
    int CurrentStock,
    decimal? DaysOfCover,
    int SuggestedQuantity,
    string? SourceMarketplace,
    string? SourceUrl,
    decimal? SourcePrice,
    string? SourceCurrency,
    decimal? AverageSalePriceMxn);

public sealed record PurchaseConfirmation(
    long CandidateId,
    int ProductId,
    int? MarketplaceAccountId);

public interface IPurchaseRadarService
{
    /// <summary>
    /// Recalcula la demanda a partir del historial de Más vendidos, busca la fuente en eBay
    /// para los candidatos más fuertes y calcula margen y cantidad sugerida.
    /// </summary>
    Task<PurchaseRadarRefreshResult> RefreshAsync(
        Guid organizationId,
        bool forceSourceSearch = false,
        CancellationToken cancellationToken = default);

    /// <summary>Ejecuta <see cref="RefreshAsync"/> para todas las organizaciones con historial reciente.</summary>
    Task<int> RefreshAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Productos propios que se venden y se están quedando sin stock.</summary>
    Task<IReadOnlyList<RestockSuggestion>> GetRestockSuggestionsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra la compra: crea (o reutiliza) producto, SKU y borrador de publicación.
    /// El stock no se suma hasta confirmar la recepción.
    /// </summary>
    Task<PurchaseConfirmation> MarkPurchasedAsync(
        Guid organizationId,
        long candidateId,
        int quantity,
        decimal? unitCostUsd,
        string? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Suma al inventario las piezas compradas cuando llegan a Juárez.</summary>
    Task MarkReceivedAsync(
        Guid organizationId,
        long candidateId,
        int? receivedQuantity,
        string? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Vincula manualmente una oferta de eBay (link o ID) cuando la búsqueda automática no acertó.
    /// </summary>
    Task SetSourceManuallyAsync(
        Guid organizationId,
        long candidateId,
        string ebayUrlOrId,
        CancellationToken cancellationToken = default);
}
