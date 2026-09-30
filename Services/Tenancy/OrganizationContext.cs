using System.Security.Claims;
using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Services.Tenancy;

public sealed class OrganizationContext : IOrganizationContext
{
    private readonly AppDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private bool _loaded;
    private Guid? _organizationId;

    public OrganizationContext(
        AppDbContext dbContext,
        IHttpContextAccessor httpContextAccessor)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<Guid?> GetCurrentOrganizationIdAsync(
        CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return _organizationId;

        _loaded = true;

        var userId = _httpContextAccessor.HttpContext?.User
            .FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return null;

        _organizationId = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.IsActive && x.Organization.IsActive)
            .OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);

        return _organizationId;
    }

    public async Task<Guid> RequireCurrentOrganizationIdAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetCurrentOrganizationIdAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "El usuario actual no pertenece a una organización activa.");
    }
}
