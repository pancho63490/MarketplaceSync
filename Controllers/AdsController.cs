using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Publicidad (Mercado Ads): campañas, anuncios y cuánto ganas con cada uno.</summary>
[Authorize]
public sealed class AdsController : Controller
{
    private readonly IOrganizationContext _organizationContext;
    private readonly AdsService _ads;

    public AdsController(IOrganizationContext organizationContext, AdsService ads)
    {
        _organizationContext = organizationContext;
        _ads = ads;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? days, string? filter, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var d = days is 7 or 30 or 90 ? days.Value : 30;
        ViewBag.Filter = filter is "bad" or "active" ? filter : "all";
        return View(await _ads.ReportAsync(org, d, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAd(int accountId, string itemId, string status, int? days, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _ads.SetAdStatusAsync(org, accountId, itemId, status, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Index), new { days });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Campaign(int accountId, long campaignId, string? status, decimal? budget, decimal? acosTarget, int? days, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _ads.UpdateCampaignAsync(org, accountId, campaignId, status, budget, acosTarget, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Index), new { days });
    }
}
