using MarketplaceSync.Services.Finance;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Dinero de Mercado Pago: lo que falta por liberar, cuándo llega y ventas con diferencias.</summary>
[Authorize]
public sealed class MoneyController : Controller
{
    private readonly IOrganizationContext _organizationContext;
    private readonly MercadoPagoService _payments;

    public MoneyController(IOrganizationContext organizationContext, MercadoPagoService payments)
    {
        _organizationContext = organizationContext;
        _payments = payments;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        return View(await _payments.SummaryAsync(org, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var differences = await _payments.ReconcileAsync(org, 60, cancellationToken);
        TempData[differences.Count > 0 ? "Warning" : "Success"] = differences.Count > 0
            ? $"Revisé los pagos: {differences.Count} {(differences.Count == 1 ? "venta tiene" : "ventas tienen")} diferencia."
            : "Revisé los pagos con Mercado Pago.";
        return RedirectToAction(nameof(Index));
    }
}
