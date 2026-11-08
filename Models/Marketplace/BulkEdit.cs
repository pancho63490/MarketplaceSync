using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Marketplace;

/// <summary>Un Excel de cambios masivos (precio, stock, estado) subido para revisar y aplicar.</summary>
public sealed class BulkEditImport
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    [MaxLength(200)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>preview, applying o done.</summary>
    [MaxLength(12)]
    public string Status { get; set; } = BulkEditStatus.Preview;

    /// <summary>Renglones con cambios (<c>BulkEditRow</c> en JSON).</summary>
    public string RowsJson { get; set; } = "[]";

    public int Total { get; set; }
    public int Applied { get; set; }
    public int Failed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public static class BulkEditStatus
{
    public const string Preview = "preview";
    public const string Applying = "applying";
    public const string Done = "done";
}
