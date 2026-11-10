using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.PostSale;

/// <summary>Mensaje rápido de postventa que escribe el vendedor. Acepta {comprador} y {producto}.</summary>
public class MessageTemplate
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    [MaxLength(60)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(350)]
    public string Text { get; set; } = string.Empty;

    public int SortOrder { get; set; }
    public int UseCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
