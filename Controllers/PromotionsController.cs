using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Campañas de Mercado Libre, descuentos propios y salir de una promoción.</summary>
[Authorize]
public sealed class PromotionsController : Controller
{
    private readonly IOrganizationContext _organizationContext;
    private readonly PromotionService _promotions;
    private readonly ICrossBorderPricingService _pricing;
    private readonly PublicationEconomics _economics;
    private readonly AppDbContext _context;

    public PromotionsController(
        IOrganizationContext organizationContext,
        PromotionService promotions,
        ICrossBorderPricingService pricing,
        PublicationEconomics economics,
        AppDbContext context)
    {
        _organizationContext = organizationContext;
        _promotions = promotions;
        _pricing = pricing;
        _economics = economics;
        _context = context;
    }

    public sealed class PromotionsIndexViewModel
    {
        public List<CampaignView> Campaigns { get; init; } = new();
        public List<DiscountedRow> Discounted { get; init; } = new();
        public decimal MinProfit { get; init; }
        public bool HasAccount { get; init; }
    }

    public sealed record DiscountedRow(
        int PublicationId, string Title, string Type, string? Status, decimal? Price, decimal? OriginalPrice,
        DateTime? EndsAt, decimal? Profit, bool CostKnown);

    public sealed class JoinRow
    {
        public int PublicationId { get; set; }
        public bool Selected { get; set; }
        public decimal? Price { get; set; }
        public string? OfferId { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var hasAccount = await _context.MarketplaceAccounts.AnyAsync(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected", cancellationToken);
        var campaigns = hasAccount ? await _promotions.CampaignsAsync(org, cancellationToken) : new List<CampaignView>();
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);

        var discounted = await _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.PromotionType != null)
            .OrderBy(p => p.PromotionEndsAt)
            .ToListAsync(cancellationToken);
        var rows = discounted.Select(p =>
        {
            var original = PromotionService.BasePrice(p) ?? 0;
            decimal? sellerPercent = p.PromotionMeliPercent is > 0 && original > 0 && p.PromotionPrice is > 0
                ? Math.Max(0, (1 - p.PromotionPrice.Value / original) * 100m - p.PromotionMeliPercent.Value)
                : null;
            var received = PromotionService.SellerPrice(p.PromotionPrice, original, p.PromotionMeliPercent, sellerPercent);
            var money = received is > 0 ? _economics.Calculate(p, p.Product, settings, received) : null;
            return new DiscountedRow(p.Id, p.Product.Title, p.PromotionType!, p.PromotionStatus, p.PromotionPrice, p.PromotionOriginalPrice,
                p.PromotionEndsAt, money?.Profit.NetProfitMxn, money is not null);
        }).ToList();

        return View(new PromotionsIndexViewModel
        {
            Campaigns = campaigns,
            Discounted = rows,
            MinProfit = settings.MinProfitAmountMxn,
            HasAccount = hasAccount
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Join(string promotionId, string type, string? subType, string? name, List<JoinRow> rows, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var selected = (rows ?? new()).Where(r => r.Selected).ToList();
        if (selected.Count == 0)
        {
            TempData["Warning"] = "Marca al menos una publicación para meterla a la campaña.";
            return RedirectToAction(nameof(Index));
        }

        var payload = new PromotionJoinPayload
        {
            PromotionId = promotionId,
            Type = type,
            SubType = string.IsNullOrWhiteSpace(subType) ? null : subType,
            Name = name ?? promotionId,
            Items = selected.Select(r => new PromotionJoinItem { PublicationId = r.PublicationId, Price = r.Price, OfferId = r.OfferId }).ToList()
        };
        var (joined, errors) = await _promotions.JoinManyAsync(org, payload, cancellationToken);
        if (joined > 0)
            TempData["Success"] = $"Entraron {joined} de {selected.Count} a «{payload.Name}». Mercado Libre la activa en unos minutos.";
        if (errors.Count > 0)
            TempData[joined > 0 ? "Warning" : "Error"] = (joined > 0 ? "No entraron: " : "Mercado Libre no aceptó ninguna. ") + string.Join(" · ", errors.Take(4));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Leave(int publicationId, string? returnTo, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _promotions.LeaveAsync(org, publicationId, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return returnTo == "edit"
            ? RedirectToAction("Edit", "Publications", new { id = publicationId }, "pe-promo-panel")
            : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Discount(int publicationId, int percent, int days, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _promotions.CreateDiscountAsync(org, publicationId, percent, days, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction("Edit", "Publications", new { id = publicationId }, "pe-promo-panel");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var count = await _promotions.RefreshAllAsync(org, 60, cancellationToken);
        TempData["Success"] = $"Revisé {count} {(count == 1 ? "publicación" : "publicaciones")} en Mercado Libre.";
        return RedirectToAction(nameof(Index));
    }
}
