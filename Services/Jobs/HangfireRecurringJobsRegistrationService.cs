using Hangfire;

namespace MarketplaceSync.Services.Jobs;

public sealed class HangfireRecurringJobsRegistrationService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly IRecurringJobManager _recurringJobManager;
    private readonly ILogger<HangfireRecurringJobsRegistrationService> _logger;

    public HangfireRecurringJobsRegistrationService(
        IRecurringJobManager recurringJobManager,
        ILogger<HangfireRecurringJobsRegistrationService> logger)
    {
        _recurringJobManager = recurringJobManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "refresh-mercadolibre-token",
                    job => job.RefreshMercadoLibreToken(),
                    "*/30 * * * *");

                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "sync-mercadolibre-orders",
                    job => job.SyncMercadoLibreOrders(),
                    "*/5 * * * *");

                // Tipo de cambio: 11:00 y 18:00 UTC (5:00 y 12:00 en Juárez; el FIX sale después de mediodía)
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "update-exchange-rate",
                    job => job.UpdateExchangeRate(),
                    "0 11,18 * * *");

                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "capture-mercadolibre-ranking-history",
                    job => job.CaptureMercadoLibreRankingHistory(),
                    "15 12 * * *");

                // Publicaciones y stock con Mercado Libre: cada 30 minutos
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "sync-mercadolibre-listings",
                    job => job.SyncMercadoLibreListings(),
                    "*/30 * * * *");

                // Preguntas de compradores (respaldo de los avisos instantáneos)
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "sync-mercadolibre-questions",
                    job => job.SyncMercadoLibreQuestions(),
                    "*/10 * * * *");

                // Categorías vigiladas: después de la captura diaria (12:15 UTC) y antes del Radar (12:45 UTC)
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "discover-watched-categories",
                    job => job.DiscoverCategories(),
                    "20 12 * * *");

                // Agente de importación: revisa con IA el Radar ya recalculado e importa lo que vale la pena (6:15 en Juárez)
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "prepare-top-candidates",
                    job => job.PrepareTopCandidates(),
                    "15 13 * * *");

                // Telegram: leer botones y respuestas cada minuto; avisos pendientes cada 5 minutos
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "telegram-poll",
                    job => job.PollTelegram(),
                    "* * * * *");
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "telegram-sweep",
                    job => job.SweepTelegramNotifications(),
                    "*/5 * * * *");

                // Paquetes a El Paso cada 2 horas y pagos de Mercado Pago cada 3 horas.
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "refresh-packages",
                    job => job.RefreshPackages(),
                    "10 */2 * * *");
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "reconcile-payments",
                    job => job.ReconcilePayments(),
                    "40 */3 * * *");
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "refresh-catalog",
                    job => job.RefreshCatalog(),
                    "50 */2 * * *");
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "sync-auto-prices",
                    job => job.SyncAutoPrices(),
                    "35 2,14 * * *");
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "refresh-listing-quality",
                    job => job.RefreshListingQuality(),
                    "15 9 * * *");

                // Avisos de fallas: cada hora (minuto 20).
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "health-check",
                    job => job.CheckHealth(),
                    "20 * * * *");

                // Mensajes de compradores y reclamos (además de las notificaciones "messages" y "claims").
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "sync-postsale",
                    job => job.SyncPostSale(),
                    "*/10 * * * *");

                // Radar de compra: después de la captura diaria del ranking (12:15 UTC)
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "refresh-purchase-radar",
                    job => job.RefreshPurchaseRadar(),
                    "45 12 * * *");

                // Source price & stock monitor: runs every 4 hours
                _recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
                    "monitor-source-prices-and-stock",
                    job => job.MonitorSourcePricesAndStock(),
                    "0 */4 * * *");

                _logger.LogInformation("Hangfire recurring jobs registered.");
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    exception,
                    "Could not register Hangfire recurring jobs. Retrying in {RetryDelaySeconds} seconds.",
                    RetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
