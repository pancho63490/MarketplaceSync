namespace MarketplaceSync.Services.Interfaces
{
    public interface IMercadoLibreAuthService
    {
        Task<string> GetValidAccessTokenAsync();

        Task<bool> RefreshTokenAsync();
    }
}