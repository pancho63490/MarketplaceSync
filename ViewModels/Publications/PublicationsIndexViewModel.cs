using MarketplaceSync.Services.Interfaces;

namespace MarketplaceSync.Web.ViewModels.Publications;

public sealed class PublicationRow
{
    public int PublicationId { get; init; }

    /// <summary>Precio para ganar lo de Costos de cruce (solo cuando el actual no llega a la ganancia mínima).</summary>
    public decimal? SuggestedPriceMxn { get; init; }
    public int ProductId { get; init; }
    public string ItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string? Permalink { get; init; }
    public string Status { get; init; } = "active";
    public decimal? Price { get; init; }
    public int? MercadoLibreStock { get; init; }

    /// <summary>Stock del inventario de la app (null si la app no controla el stock de este producto).</summary>
    public int? AppStock { get; init; }
    public int SkuCount { get; init; }
    public bool HasRemoteVariations { get; init; }
    public int? Sold { get; init; }
    public bool CostKnown { get; init; }
    public decimal? CostValue { get; init; }
    public string? CostCurrency { get; init; }
    public decimal? LandedCostMxn { get; init; }
    public CrossBorderProfitEstimate? Profit { get; init; }
    public KitSuggestion? Kit { get; init; }
    public bool FeeIsReal { get; init; }
    public DateTime? LastSyncedAt { get; init; }
    public string? LastStockPushError { get; init; }
    public string? AccountName { get; init; }

    public bool StockMismatch => AppStock.HasValue && MercadoLibreStock.HasValue && AppStock != MercadoLibreStock;
}

public sealed class PublicationsIndexViewModel
{
    public IReadOnlyList<PublicationRow> Rows { get; init; } = Array.Empty<PublicationRow>();
    public string? Filter { get; init; }
    public string? Search { get; init; }
    public int ActiveCount { get; init; }
    public int PausedCount { get; init; }
    public int NoCostCount { get; init; }
    public int LossCount { get; init; }
    public int MismatchCount { get; init; }
    public decimal PotentialProfitMxn { get; init; }
    public int UnitsListed { get; init; }
    public DateTime? LastSyncedAt { get; init; }
    public bool HasConnectedAccount { get; init; }
}
