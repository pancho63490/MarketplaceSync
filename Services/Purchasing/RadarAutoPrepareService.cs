using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Purchasing;

public sealed record AutoPrepareResult(int Prepared, int Ready, int Remaining, IReadOnlyList<string> Errors);

/// <summary>
/// Toma los mejores productos del Radar (más demanda y más ganancia) y los deja preparados con IA:
/// título, descripción, categoría, atributos y precio. No publica: eso pasa al recibir la compra.
/// Respeta el máximo semanal de la configuración para no gastar en productos que no se van a comprar.
/// </summary>
public sealed class RadarAutoPrepareService
{
    private readonly AppDbContext _context;
    private readonly IPurchaseRadarService _radar;
    private readonly IListingAutopilotService _autopilot;
    private readonly ICrossBorderPricingService _pricing;
    private readonly ILogger<RadarAutoPrepareService> _logger;

    public RadarAutoPrepareService(
        AppDbContext context,
        IPurchaseRadarService radar,
        IListingAutopilotService autopilot,
        ICrossBorderPricingService pricing,
        ILogger<RadarAutoPrepareService> logger)
    {
        _context = context;
        _radar = radar;
        _autopilot = autopilot;
        _pricing = pricing;
        _logger = logger;
    }

    public async Task<AutoPrepareResult> PrepareTopAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var weekly = Math.Clamp(settings.AutoPrepareWeekly, 0, 50);
        if (weekly == 0)
            return new AutoPrepareResult(0, 0, 0, Array.Empty<string>());

        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var preparedThisWeek = await _context.PurchaseCandidates.CountAsync(
            x => x.OrganizationId == organizationId && x.PreparedAt >= weekAgo, cancellationToken);
        var remaining = weekly - preparedThisWeek;
        if (remaining <= 0)
            return new AutoPrepareResult(0, 0, 0, Array.Empty<string>());

        var picks = await _context.PurchaseCandidates
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.Status == PurchaseCandidateStatus.Suggested &&
                        x.ProductId == null &&
                        x.DemandScore > 0 &&
                        x.EstimatedNetProfitMxn > 0)
            .OrderByDescending(x => x.DemandScore)
            .ThenByDescending(x => x.EstimatedNetProfitMxn)
            .Take(remaining)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        int prepared = 0, ready = 0;
        var errors = new List<string>();
        foreach (var candidateId in picks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var productId = await _radar.PrepareAsync(organizationId, candidateId, cancellationToken);
                _context.ChangeTracker.Clear();
                var result = await _autopilot.RunAsync(organizationId, productId, publishIfReady: false, cancellationToken);
                prepared++;
                // Sin stock nunca queda "listo para publicar"; se cuenta como listo si solo falta el stock.
                if (result.Missing.All(m => m.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
                                            m.Contains("piezas", StringComparison.OrdinalIgnoreCase)))
                    ready++;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Auto-prepare failed for candidate {CandidateId}.", candidateId);
                errors.Add(exception.Message);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }

        return new AutoPrepareResult(prepared, ready, Math.Max(0, remaining - prepared), errors);
    }
}
