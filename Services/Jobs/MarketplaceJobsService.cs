using MarketplaceSync.Services.Interfaces;
using Hangfire;

namespace MarketplaceSync.Services.Jobs
{
    public class MarketplaceJobsService
    {
        private readonly IMercadoLibreAuthService _authService;
        private readonly IMercadoLibreOrderSyncService _orderSyncService;
        private readonly IMarketRankingHistoryService _rankingHistoryService;
        private readonly ISourcePriceMonitorService _sourcePriceMonitor;
        private readonly IPurchaseRadarService _purchaseRadarService;
        private readonly MarketplaceSync.Services.MercadoLibre.IMercadoLibreListingSyncService _listingSync;

        public MarketplaceJobsService(
            IMercadoLibreAuthService authService,
            IMercadoLibreOrderSyncService orderSyncService,
            IMarketRankingHistoryService rankingHistoryService,
            ISourcePriceMonitorService sourcePriceMonitor,
            IPurchaseRadarService purchaseRadarService,
            MarketplaceSync.Services.MercadoLibre.IMercadoLibreListingSyncService listingSync)
        {
            _authService = authService;
            _orderSyncService = orderSyncService;
            _rankingHistoryService = rankingHistoryService;
            _sourcePriceMonitor = sourcePriceMonitor;
            _purchaseRadarService = purchaseRadarService;
            _listingSync = listingSync;
        }

        public async Task RefreshMercadoLibreToken()
        {
            await _authService.RefreshExpiringTokensAsync();
        }

        [DisableConcurrentExecution(timeoutInSeconds: 300)]
        public async Task SyncMercadoLibreOrders()
        {
            await _orderSyncService.SyncAllAccountsAsync();
        }

        [DisableConcurrentExecution(timeoutInSeconds: 1800)]
        public async Task CaptureMercadoLibreRankingHistory()
        {
            await _rankingHistoryService.CaptureAllAsync();
        }

        /// <summary>
        /// Checks source price and stock for all monitored products every 4 hours.
        /// Updates Mercado Libre listings automatically on significant changes.
        /// </summary>
        [DisableConcurrentExecution(timeoutInSeconds: 3600)]
        public async Task MonitorSourcePricesAndStock()
        {
            await _sourcePriceMonitor.MonitorAllProductsAsync();
        }
    
        /// <summary>
        /// Recalcula el Radar de compra después de la captura diaria de Más vendidos.
        /// </summary>
        [DisableConcurrentExecution(timeoutInSeconds: 3600)]
        public async Task RefreshPurchaseRadar()
        {
            await _purchaseRadarService.RefreshAllAsync();
        }
    
        /// <summary>Lee publicaciones de Mercado Libre y corrige diferencias de stock.</summary>
        [DisableConcurrentExecution(timeoutInSeconds: 1800)]
        public async Task SyncMercadoLibreListings()
        {
            await _listingSync.SyncAllAsync();
        }

        /// <summary>Envía el stock de un producto a Mercado Libre (después de una venta, recepción o ajuste).</summary>
        [AutomaticRetry(Attempts = 3)]
        public async Task PushStockForProduct(int productId)
        {
            await _listingSync.PushStockAsync(productId);
        }
    }
}
