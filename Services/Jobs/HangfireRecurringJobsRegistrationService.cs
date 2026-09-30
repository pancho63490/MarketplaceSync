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
