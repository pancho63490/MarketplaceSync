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
