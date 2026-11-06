using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.Questions;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Navigation;

public sealed record NavBadges(int PendingQuestions, int OrdersToShip, int LateOrders, int PostSale = 0, int OpenClaims = 0);

/// <summary>Contadores del menú (preguntas sin responder, pedidos por enviar). Una consulta por petición.</summary>
public sealed class NavBadgeService
{
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly ILogger<NavBadgeService> _logger;
    private NavBadges? _cached;

    public NavBadgeService(AppDbContext context, IOrganizationContext organizationContext, ILogger<NavBadgeService> logger)
    {
        _context = context;
        _organizationContext = organizationContext;
        _logger = logger;
    }

    public async Task<NavBadges> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
            return _cached;
        try
        {
            var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
            var questions = await _context.BuyerQuestions.CountAsync(q => q.OrganizationId == org && q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);
            var orders = _context.SalesOrders.Where(o => o.OrganizationId == org);
            var toShip = await orders.ToShip().CountAsync(cancellationToken);
            var late = await orders.LateToShip(DateTime.UtcNow).CountAsync(cancellationToken);
            var since = DateTime.UtcNow.AddDays(-45);
            var recent = await _context.OrderMessages.AsNoTracking()
                .Where(m => m.OrganizationId == org && m.SentAt >= since)
                .Select(m => new { m.PackId, m.FromSeller, m.SentAt })
                .ToListAsync(cancellationToken);
            var unanswered = recent.GroupBy(m => m.PackId).Count(g => !g.OrderByDescending(m => m.SentAt).First().FromSeller);
            var claims = await _context.OrderClaims.CountAsync(c => c.OrganizationId == org && c.Status == "opened", cancellationToken);
            _cached = new NavBadges(questions, toShip, late, unanswered + claims, claims);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Nav badges unavailable.");
            _cached = new NavBadges(0, 0, 0);
        }
        return _cached;
    }
}
