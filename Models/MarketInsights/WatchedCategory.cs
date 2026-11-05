using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.MarketInsights;

/// <summary>
/// Categoría de Mercado Libre que la app explora sola para encontrar qué comprar.
/// Las que eliges tú (IsSelected) se expanden a sus subcategorías finales, que son las que se revisan cada día.
/// </summary>
public sealed class WatchedCategory
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [MaxLength(30)]
    public string CategoryId { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    /// <summary>"Accesorios para Vehículos › Refacciones Autos y Camionetas › Frenos".</summary>
    [MaxLength(600)]
    public string? PathText { get; set; }

    /// <summary>La eligió el usuario (true) o salió al expandir una elegida (false).</summary>
    public bool IsSelected { get; set; }

    /// <summary>Categoría elegida de la que salió esta subcategoría.</summary>
    public int? ParentWatchedCategoryId { get; set; }
    public WatchedCategory? ParentWatchedCategory { get; set; }

    public bool IsLeaf { get; set; }
    public bool Enabled { get; set; } = true;

    public DateTime? LastExpandedAt { get; set; }
    public DateTime? LastScannedAt { get; set; }
    public int? LastItemsFound { get; set; }

    [MaxLength(300)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
