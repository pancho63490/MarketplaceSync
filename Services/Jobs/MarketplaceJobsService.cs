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
        private readonly MarketplaceSync.Services.Agents.ImportAgentService _agent;
        private readonly MarketplaceSync.Services.Ai.IListingAutopilotService _autopilot;
        private readonly MarketplaceSync.Services.Notifications.TelegramNotificationService _telegram;
        private readonly ILogger<MarketplaceJobsService> _logger;
        private readonly IServiceProvider _serviceProvider;

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
            MarketplaceSync.Services.Agents.ImportAgentService agent,
            IServiceProvider serviceProvider,
            MarketplaceSync.Services.Ai.IListingAutopilotService autopilot,
            MarketplaceSync.Services.Notifications.TelegramNotificationService telegram,
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
            _agent = agent;
            _serviceProvider = serviceProvider;
            _autopilot = autopilot;
            _telegram = telegram;
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
            await _telegram.SweepAllAsync();
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
            await _telegram.SweepAllAsync();
        }

        /// <summary>Aviso de Mercado Libre: nueva pregunta.</summary>
        [AutomaticRetry(Attempts = 3)]
        public async Task ProcessQuestionNotification(int marketplaceAccountId, string questionId)
        {
            await _questions.ProcessQuestionAsync(marketplaceAccountId, questionId);
            await _telegram.SweepAllAsync();
        }

        /// <summary>Aviso de Mercado Libre: pedido nuevo o cambiado.</summary>
        [AutomaticRetry(Attempts = 3)]
        [DisableConcurrentExecution(timeoutInSeconds: 300)]
        public async Task SyncOrdersForOrganization(Guid organizationId)
        {
            await _orderSyncService.SyncAllAccountsAsync(organizationId);
            await _telegram.SweepAsync(organizationId);
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

        /// <summary>
        /// Agente de importación diario: revisa con IA los candidatos del Radar e importa los que valen la pena
        /// (máximo semanal de la configuración). Sin IA usa la regla de más demanda y ganancia.
        /// </summary>
        [DisableConcurrentExecution(timeoutInSeconds: 3600)]
        public async Task PrepareTopCandidates()
        {
            foreach (var organizationId in await _agent.GetOrganizationsAsync())
            {
                try
                {
                    await _agent.RunAsync(organizationId, "schedule");
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
            await _agent.RunAsync(organizationId, "manual");
        }

        /// <summary>Encargo al agente (desde la app o con /encargo en Telegram): busca en Mercado Libre y eBay e importa lo que convenga.</summary>
        [AutomaticRetry(Attempts = 0)]
        public async Task RunImportAgentRequest(Guid organizationId, string request)
        {
            await _agent.RunAsync(organizationId, "manual", request);
        }

        /// <summary>Botón "Ejecutar ahora" del agente: solo revisa e importa (no vuelve a explorar).</summary>
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 1800)]
        public async Task RunImportAgent(Guid organizationId)
        {
            await _agent.RunAsync(organizationId, "manual");
        }

        /// <summary>Al recibir una compra: publica el producto si ya quedó completo.</summary>
        [AutomaticRetry(Attempts = 2)]
        public async Task PublishAfterReceipt(Guid organizationId, int productId)
        {
            var result = await _autopilot.RunAsync(organizationId, productId, publishIfReady: true);
            _logger.LogInformation("Publish after receipt for product {ProductId}: published={Published}, missing={Missing}",
                productId, result.Published, string.Join("; ", result.Missing));
        }

        /// <summary>Lee los mensajes y botones que el vendedor mandó al bot de Telegram.</summary>
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 120)]
        public async Task PollTelegram()
        {
            await _telegram.PollAllAsync();
        }

        /// <summary>Manda por Telegram lo que no se ha avisado (preguntas, ventas, agotados, resumen diario).</summary>
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 300)]
        public async Task SweepTelegramNotifications()
        {
            await _telegram.SweepAllAsync();
        }

        /// <summary>Fotos: revisa las portadas sin revisar.</summary>
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 1800)]
        public async Task CheckPhotos(Guid organizationId)
        {
            var photos = _serviceProvider.GetRequiredService<MarketplaceSync.Services.Photos.PhotoStudioService>();
            await photos.CheckPendingAsync(organizationId, 300);
        }

        /// <summary>Fotos: pone fondo blanco a las que se pueden arreglar (hasta 40).</summary>
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 1800)]
        public async Task FixPhotos(Guid organizationId, bool updateListings)
        {
            var photos = _serviceProvider.GetRequiredService<MarketplaceSync.Services.Photos.PhotoStudioService>();
            await photos.FixAllAsync(organizationId, updateListings);
        }

        /// <summary>Competencia: cuenta qué tiendas aparecen más en los más vendidos de las categorías vigiladas.</summary>
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 900)]
        public async Task ScanCompetitors(Guid organizationId)
        {
            var competitors = _serviceProvider.GetRequiredService<MarketplaceSync.Services.MarketInsights.CompetitorService>();
            await competitors.ScanLeadersAsync(organizationId);
        }

        /// <summary>Mensajes de compradores y reclamos (cada 10 min y al llegar una notificación de Mercado Libre).</summary>
        [DisableConcurrentExecution(timeoutInSeconds: 900)]
        public async Task SyncPostSale()
        {
            var postSale = _serviceProvider.GetRequiredService<MarketplaceSync.Services.PostSale.PostSaleService>();
            foreach (var organizationId in await postSale.GetOrganizationsAsync())
            {
                try
                {
                    await postSale.SyncAsync(organizationId);
                    await _telegram.SweepAsync(organizationId);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Post-sale sync failed for {OrganizationId}.", organizationId);
                }
            }
        }

        [AutomaticRetry(Attempts = 1)]
        public async Task SyncPostSaleForOrganization(Guid organizationId)
        {
            var postSale = _serviceProvider.GetRequiredService<MarketplaceSync.Services.PostSale.PostSaleService>();
            await postSale.SyncAsync(organizationId);
            await _telegram.SweepAsync(organizationId);
        }
    }
}
