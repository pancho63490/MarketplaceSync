using Hangfire;
using MarketplaceSync.Services.Jobs;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>
/// Programa en segundo plano el envío de stock a Mercado Libre después de un cambio de inventario,
/// para que la pantalla responda al instante y un error de Mercado Libre no bloquee la operación.
/// </summary>
public interface IStockSyncScheduler
{
    void RequestPush(IEnumerable<int> productIds);
}

public sealed class StockSyncScheduler : IStockSyncScheduler
{
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<StockSyncScheduler> _logger;

    public StockSyncScheduler(IBackgroundJobClient jobs, ILogger<StockSyncScheduler> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    public void RequestPush(IEnumerable<int> productIds)
    {
        foreach (var productId in productIds.Distinct())
        {
            try
            {
                _jobs.Enqueue<MarketplaceJobsService>(job => job.PushStockForProduct(productId));
            }
            catch (Exception exception)
            {
                // Si la cola no está disponible, la sincronización programada lo corregirá.
                _logger.LogWarning(exception, "Could not enqueue stock push for product {ProductId}.", productId);
            }
        }
    }
}
