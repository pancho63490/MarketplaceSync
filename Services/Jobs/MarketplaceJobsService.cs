using MarketplaceSync.Services.Interfaces;

namespace MarketplaceSync.Services.Jobs
{
    public class MarketplaceJobsService
    {
        private readonly IMercadoLibreAuthService _authService;

        public MarketplaceJobsService(
            IMercadoLibreAuthService authService)
        {
            _authService = authService;
        }

        public async Task RefreshMercadoLibreToken()
        {
            await _authService.RefreshTokenAsync();
        }
    }
}