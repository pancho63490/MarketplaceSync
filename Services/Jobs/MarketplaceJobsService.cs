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
        private readonly MarketplaceSync.Services.MercadoLibre.IMercadoLibreQuestionService _questions;
        private readonly MarketplaceSync.Services.MarketInsights.CategoryDiscoveryService _discovery;
        private readonly MarketplaceSync.Services.Purchasing.RadarAutoPrepareService _autoPrepare;
        private readonly MarketplaceSync.Services.Ai.IListingAutopilotService _autopilot;
        private readonly ILogger<MarketplaceJobsService> _logger;

        public MarketplaceJobsService(
            IMercadoLibreAuthService authService,
            IMercadoLibreOrderSyncService orderSyncService,
            IMarketRankingHistoryService rankingHistoryService,
            ISourcePriceMonitorService sourcePriceMonitor,
            IPurchaseRadarService purchaseRadarService,
            MarketplaceSync.Services.MercadoLibre.IMercadoLibreListingSyncService listingSync,
            MarketplaceSync.Services.MercadoLibre.IMercadoLibreQuestionService questions,
            MarketplaceSync.Services.MarketInsights.CategoryDiscoveryService discovery,
            MarketplaceSync.Services.Purchasing.RadarAutoPrepareService autoPrepare,
            MarketplaceSync.Services.Ai.IListingAutopilotService autopilot,
            ILogger<MarketplaceJobsService> logger)
        {
            _authService = authService;
            _orderSyncService = orderSyncService;
            _rankingHistoryService = rankingHistoryService;
            _sourcePriceMonitor = sourcePriceMonitor;
            _purchaseRadarService = purchaseRadarService;
            _listingSync = listingSync;
            _questions = questions;
            _discovery = discovery;
            _autoPrepare = autoPrepare;
            _autopilot = autopilot;
            _logger = logger;
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

        /// <summary>Trae preguntas sin responder y prepara respuestas con IA.</summary>
        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task SyncMercadoLibreQuestions()
        {
            await _questions.SyncAllAsync();
        }

        /// <summary>Aviso de Mercado Libre: nueva pregunta.</summary>
        [AutomaticRetry(Attempts = 3)]
        public async Task ProcessQuestionNotification(int marketplaceAccountId, string questionId)
        {
            await _questions.ProcessQuestionAsync(marketplaceAccountId, questionId);
        }

        /// <summary>Aviso de Mercado Libre: pedido nuevo o cambiado.</summary>
        [AutomaticRetry(Attempts = 3)]
        [DisableConcurrentExecution(timeoutInSeconds: 300)]
        public async Task SyncOrdersForOrganization(Guid organizationId)
        {
            await _orderSyncService.SyncAllAccountsAsync(organizationId);
        }

        /// <summary>Aviso de Mercado Libre: cambió una publicación.</summary>
        [AutomaticRetry(Attempts = 2)]
        [DisableConcurrentExecution(timeoutInSeconds: 900)]
        public async Task SyncListingsForOrganization(Guid organizationId)
        {
            await _listingSync.SyncOrganizationAsync(organizationId);
        }

        /// <summary>Explora las categorías vigiladas de todas las organizaciones (antes del Radar diario).</summary>
        [DisableConcurrentExecution(timeoutInSeconds: 3600)]
        public async Task DiscoverCategories()
        {
            foreach (var organizationId in await _discovery.GetOrganizationsWithCategoriesAsync())
            {
                try
                {
                    var result = await _discovery.ScanOrganizationAsync(organizationId);
                    _logger.LogInformation("Discovery for {OrganizationId}: {Scanned} categories, {WithRanking} with ranking.",
                        organizationId, result.Scanned, result.WithRanking);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Discovery failed for {OrganizationId}.", organizationId);
                }
            }
        }

        /// <summary>Deja preparados con IA los mejores del Radar (máximo semanal de la configuración).</summary>
        [DisableConcurrentExecution(timeoutInSeconds: 3600)]
        public async Task PrepareTopCandidates()
        {
            foreach (var organizationId in await _discovery.GetOrganizationsWithCategoriesAsync())
            {
                try
                {
                    await _autoPrepare.PrepareTopAsync(organizationId);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Auto-prepare failed for {OrganizationId}.", organizationId);
                }
            }
        }

        /// <summary>Botón "Explorar ahora": explora, recalcula el Radar y prepara los mejores de una organización.</summary>
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 1800)]
        public async Task DiscoverForOrganization(Guid organizationId)
        {
            await _discovery.ScanOrganizationAsync(organizationId, 30);
            await _purchaseRadarService.RefreshAsync(organizationId);
            await _autoPrepare.PrepareTopAsync(organizationId);
        }

        /// <summary>Al recibir una compra: publica el producto si ya quedó completo.</summary>
        [AutomaticRetry(Attempts = 2)]
        public async Task PublishAfterReceipt(Guid organizationId, int productId)
        {
            var result = await _autopilot.RunAsync(organizationId, productId, publishIfReady: true);
            _logger.LogInformation("Publish after receipt for product {ProductId}: published={Published}, missing={Missing}",
                productId, result.Published, string.Join("; ", result.Missing));
        }
    }
}
