using System.Globalization;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Calidad y catálogo: qué publicaciones pierden el botón de compra y qué le falta a cada una según Mercado Libre.</summary>
[Authorize]
public sealed class QualityController : Controller
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly IOrganizationContext _organizationContext;
    private readonly CatalogCompetitionService _catalog;
    private readonly ListingQualityService _quality;
    private readonly MercadoLibreItemEditorService _editor;
    private readonly PublicationEconomics _economics;
    private readonly ICrossBorderPricingService _pricing;
    private readonly AppDbContext _context;

    public QualityController(IOrganizationContext organizationContext, CatalogCompetitionService catalog, ListingQualityService quality,
        MercadoLibreItemEditorService editor, PublicationEconomics economics, ICrossBorderPricingService pricing, AppDbContext context)
    {
        _organizationContext = organizationContext;
        _catalog = catalog;
        _quality = quality;
        _editor = editor;
        _economics = economics;
        _pricing = pricing;
        _context = context;
    }

    public sealed class QualityViewModel
    {
        public List<CatalogRow> Catalog { get; init; } = new();
        public List<MarketplacePublication> Quality { get; init; } = new();
        public int ActiveCount { get; init; }
        public int QualityChecked { get; init; }
        public int Professional { get; init; }
        public int Standard { get; init; }
        public int Basic { get; init; }
        public double? AverageScore { get; init; }
        public string Level { get; init; } = "all";
        public ReviewsSummary? Reviews { get; init; }
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? level, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var active = _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.IsPublished && p.Status == "active");
        var withScore = active.Where(p => p.QualityScore != null);

        var query = active.Include(p => p.Product).Where(p => p.QualityScore != null);
        level = level is "Basic" or "Standard" or "Professional" ? level : "all";
        if (level != "all") query = query.Where(p => p.QualityLevel == level);

        return View(new QualityViewModel
        {
            Catalog = await _catalog.RowsAsync(org, cancellationToken),
            Quality = await query.OrderBy(p => p.QualityScore).ThenByDescending(p => p.SoldQuantity ?? 0).Take(60).ToListAsync(cancellationToken),
            ActiveCount = await active.CountAsync(cancellationToken),
            QualityChecked = await withScore.CountAsync(cancellationToken),
            Professional = await withScore.CountAsync(p => p.QualityLevel == "Professional", cancellationToken),
            Standard = await withScore.CountAsync(p => p.QualityLevel == "Standard", cancellationToken),
            Basic = await withScore.CountAsync(p => p.QualityLevel == "Basic", cancellationToken),
            AverageScore = await withScore.AverageAsync(p => (double?)p.QualityScore, cancellationToken),
            Level = level,
            Reviews = await HttpContext.RequestServices.GetRequiredService<ReviewService>().SummaryAsync(org, cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var changes = await _catalog.RefreshAsync(org, 80, force: true, cancellationToken);
        var quality = await _quality.RefreshAsync(org, 40, cancellationToken);
        await HttpContext.RequestServices.GetRequiredService<ReviewService>().RefreshAsync(org, 60, force: true, cancellationToken);
        var losing = changes.Count(c => c.NewStatus == CatalogCompetitionService.Losing);
        TempData[losing > 0 ? "Warning" : "Success"] =
            $"Revisé el catálogo, las opiniones y la calidad de {quality} {(quality == 1 ? "publicación" : "publicaciones")}." +
            (losing > 0 ? $" {losing} {(losing == 1 ? "empezó" : "empezaron")} a perder en catálogo." : "");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Baja el precio al que gana en catálogo (con confirmación en la pantalla). No baja si deja pérdida.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MatchPrice(int publicationId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var publication = await _context.MarketplacePublications.Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == org, cancellationToken);
        if (publication is null)
            return NotFound();
        if (publication.CatalogStatus != CatalogCompetitionService.Losing || publication.CatalogPriceToWin is not > 0)
        {
            TempData["Warning"] = "Esa publicación ya no está perdiendo o Mercado Libre no dio precio para ganar. Revisa de nuevo.";
            return RedirectToAction(nameof(Index), null, null, "catalogo");
        }

        var target = Math.Floor(publication.CatalogPriceToWin.Value);
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var atTarget = _economics.Calculate(publication, publication.Product, settings, target);
        if (atTarget is { Profit.NetProfitMxn: <= 0 })
        {
            TempData["Error"] = $"A {target.ToString("C0", Mx)} perderías {(-atTarget.Profit.NetProfitMxn).ToString("C0", Mx)} por pieza; no cambié el precio.";
            return RedirectToAction(nameof(Index), null, null, "catalogo");
        }

        var result = await _editor.UpdateAsync(org, publication.Id,
            new ItemEdit { Price = target.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
        if (!result.Ok)
        {
            TempData["Error"] = "Mercado Libre no aceptó el precio: " + string.Join(" ", result.Errors);
            return RedirectToAction(nameof(Index), null, null, "catalogo");
        }

        await _context.MarketplacePublications.Where(p => p.Id == publication.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.CatalogCheckedAt, (DateTime?)null), cancellationToken);
        TempData["Success"] = $"Precio de «{publication.Product.Title}» bajado a {target.ToString("C0", Mx)}" +
                              (atTarget is null ? "." : $"; ganas {atTarget.Profit.NetProfitMxn.ToString("C0", Mx)} por pieza.") +
                              " En unos minutos Mercado Libre recalcula quién gana.";
        return RedirectToAction(nameof(Index), null, null, "catalogo");
    }
}
