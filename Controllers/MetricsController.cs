using MarketplaceSync.Services.Metrics;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketplaceSync.Web.Controllers;

/// <summary>KPIs de la tienda: ventas, ganancia, reputación, visitas, conversión y preguntas.</summary>
[Authorize]
public sealed class MetricsController : Controller
{
    private readonly MetricsService _metrics;
    private readonly IOrganizationContext _organizationContext;

    public MetricsController(MetricsService metrics, IOrganizationContext organizationContext)
    {
        _metrics = metrics;
        _organizationContext = organizationContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int days = 30, CancellationToken cancellationToken = default)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        return View(await _metrics.GetAsync(organizationId, days, cancellationToken));
    }
}
