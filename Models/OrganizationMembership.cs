using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace MarketplaceSync.Web.Models;

public static class OrganizationRoles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Operator = "Operator";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { Owner, Admin, Operator },
        StringComparer.OrdinalIgnoreCase);
}

public class OrganizationMembership
{
    public Guid OrganizationId { get; set; }

    public Organization Organization { get; set; } = null!;

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public IdentityUser User { get; set; } = null!;

    [Required]
    [MaxLength(30)]
    public string Role { get; set; } = OrganizationRoles.Operator;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
