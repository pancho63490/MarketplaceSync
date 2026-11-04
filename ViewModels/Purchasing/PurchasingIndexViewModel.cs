using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Models.Purchasing;

namespace MarketplaceSync.Web.ViewModels.Purchasing;

public sealed class PurchasingIndexViewModel
{
    public string Tab { get; init; } = "buy";

    /// <summary>Validados: hay demanda, fuente y margen.</summary>
    public IReadOnlyList<PurchaseCandidate> ToBuy { get; init; } = Array.Empty<PurchaseCandidate>();

    /// <summary>Con demanda pero sin fuente confiable o sin margen al precio del mercado.</summary>
    public IReadOnlyList<PurchaseCandidate> ToReview { get; init; } = Array.Empty<PurchaseCandidate>();

    /// <summary>Comprados, pendientes de recibir en Juárez.</summary>
    public IReadOnlyList<PurchaseCandidate> InTransit { get; init; } = Array.Empty<PurchaseCandidate>();

    public IReadOnlyList<PurchaseCandidate> Discarded { get; init; } = Array.Empty<PurchaseCandidate>();

    public IReadOnlyList<RestockSuggestion> Restock { get; init; } = Array.Empty<RestockSuggestion>();

    public decimal ShoppingListTotalUsd { get; init; }
    public decimal ShoppingListExpectedProfitMxn { get; init; }
    public int ShoppingListUnits { get; init; }
    public decimal UsdToMxnRate { get; init; }
    public decimal MinProfitAmountMxn { get; init; }
    public DateTime? LastEvaluatedAt { get; init; }
    public bool HasRankingHistory { get; init; }
}
