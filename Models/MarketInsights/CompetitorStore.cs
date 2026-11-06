using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.MarketInsights;

/// <summary>Tienda de Mercado Libre que vende en tus categorías (detectada en los más vendidos o seguida a mano).</summary>
public sealed class CompetitorStore
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }

    [MaxLength(30)]
    public string SellerId { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Nickname { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? Permalink { get; set; }

    /// <summary>Nivel del termómetro (1 a 5) si Mercado Libre lo muestra.</summary>
    public int? ReputationLevel { get; set; }

    /// <summary>platinum, gold, silver (MercadoLíder) o null.</summary>
    [MaxLength(20)]
    public string? PowerSeller { get; set; }

    /// <summary>Ventas totales que Mercado Libre muestra en su reputación.</summary>
    public int? TotalSales { get; set; }

    /// <summary>Cuántas veces aparece en los más vendidos de tus categorías en el último escaneo.</summary>
    public int TopAppearances { get; set; }

    /// <summary>Mejor posición que tiene en algún ranking.</summary>
    public int? BestPosition { get; set; }

    [MaxLength(500)]
    public string? Categories { get; set; }

    /// <summary>La seguiste tú (no solo la detectó el escaneo).</summary>
    public bool Followed { get; set; }

    /// <summary>Ocultada: no volver a mostrarla en líderes.</summary>
    public bool Hidden { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }
}
