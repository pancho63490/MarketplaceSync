using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.Models.Purchasing;

/// <summary>
/// Producto que el Radar de compra propone traer desde EE.UU.
/// Une la demanda observada en Mercado Libre (historial de Más vendidos) con la mejor
/// oferta encontrada en la fuente y el margen calculado con las reglas de costeo
/// El Paso ➔ Juárez de la organización.
/// </summary>
public sealed class PurchaseCandidate
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    // ── Demanda en Mercado Libre ─────────────────────────────
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string MarketExternalId { get; set; } = string.Empty;
    public string MarketName { get; set; } = string.Empty;
    public string? MarketBrand { get; set; }
    public string? MarketModel { get; set; }
    public string? MarketImageUrl { get; set; }
    public string? MarketPermalink { get; set; }
    public decimal? MarketPriceMxn { get; set; }
    public int BestPosition { get; set; }
    public int? LatestPosition { get; set; }
    public int DaysInTop { get; set; }
    public int SnapshotDaysObserved { get; set; }
    public string Trend { get; set; } = PurchaseTrend.Stable;
    public int DemandScore { get; set; }

    // ── Fuente (EE.UU.) ──────────────────────────────────────
    public string? SourceMarketplace { get; set; }
    public string? SourceProductId { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceTitle { get; set; }
    public string? SourceImageUrl { get; set; }
    public string? SourceCondition { get; set; }
    public decimal? SourcePriceUsd { get; set; }
    public decimal? SourceShippingUsd { get; set; }
    public string? SourceSearchQuery { get; set; }
    public int MatchConfidence { get; set; }
    public DateTime? LastSourceSearchAt { get; set; }

    // ── Rentabilidad ─────────────────────────────────────────
    public decimal? LandedCostMxn { get; set; }
    public decimal? EstimatedNetProfitMxn { get; set; }
    public decimal? EstimatedMarginPercent { get; set; }
    public decimal? RecommendedListingPriceMxn { get; set; }
    public int SuggestedQuantity { get; set; }

    // ── Flujo de compra ──────────────────────────────────────
    public string Status { get; set; } = PurchaseCandidateStatus.Suggested;
    public int? PurchasedQuantity { get; set; }
    public decimal? PurchasedUnitCostUsd { get; set; }
    /// <summary>Cuándo la app lo dejó preparado (producto con título, atributos y precio) antes de comprarlo.</summary>
    public DateTime? PreparedAt { get; set; }

    public DateTime? PurchasedAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
    public int? MarketplaceAccountId { get; set; }
    public MarketplaceAccount? MarketplaceAccount { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastEvaluatedAt { get; set; } = DateTime.UtcNow;
}

public static class PurchaseCandidateStatus
{
    /// <summary>Demanda y margen validados: conviene comprar.</summary>
    public const string Suggested = "Suggested";
    /// <summary>No se encontró una oferta confiable en la fuente.</summary>
    public const string NoSource = "NoSource";
    /// <summary>Hay fuente, pero al precio de mercado no deja la ganancia mínima.</summary>
    public const string LowMargin = "LowMargin";
    /// <summary>El vendedor decidió no comprarlo.</summary>
    public const string Discarded = "Discarded";
    /// <summary>Comprado, en camino a Juárez.</summary>
    public const string Purchased = "Purchased";
    /// <summary>Recibido: el stock ya se sumó al inventario.</summary>
    public const string Received = "Received";

    public static readonly IReadOnlySet<string> Reevaluable =
        new HashSet<string>(StringComparer.Ordinal) { Suggested, NoSource, LowMargin };
}

public static class PurchaseTrend
{
    public const string Rising = "Rising";
    public const string Stable = "Stable";
    public const string Falling = "Falling";
    public const string New = "New";
    public const string Exited = "Exited";
}
