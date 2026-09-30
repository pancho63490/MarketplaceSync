namespace MarketplaceSync.Web.Services.Tenancy;

public interface IOrganizationContext
{
    Task<Guid?> GetCurrentOrganizationIdAsync(CancellationToken cancellationToken = default);

    Task<Guid> RequireCurrentOrganizationIdAsync(CancellationToken cancellationToken = default);
}
