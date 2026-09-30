namespace MarketplaceSync.Services.Interfaces
{
    public interface IMercadoLibreAuthService
    {
        Task<string> GetValidAccessTokenAsync(Guid organizationId, int marketplaceAccountId);

        Task<bool> RefreshTokenAsync(int tokenId);

        Task<int> RefreshExpiringTokensAsync();
    }
}
