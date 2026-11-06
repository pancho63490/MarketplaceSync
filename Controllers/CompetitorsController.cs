using System.Globalization;
using Hangfire;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Jobs;
using MarketplaceSync.Services.MarketInsights;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.MarketInsights;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

public sealed class CompetitorsIndexViewModel
{
    public IReadOnlyList<CompetitorStore> Leaders { get; init; } = Array.Empty<CompetitorStore>();
    public IReadOnlyList<CompetitorStore> Followed { get; init; } = Array.Empty<CompetitorStore>();
    public DateTime? LastScan { get; init; }
    public int WatchedCategories { get; init; }
    public bool AiConfigured { get; init; }
}

public sealed class CompetitorStoreViewModel
{
    public required CompetitorStore Store { get; init; }
    public IReadOnlyList<CompetitorItem> Items { get; init; } = Array.Empty<CompetitorItem>();
    public int Total { get; init; }
    public string? Error { get; init; }
    public string? Query { get; init; }
    public int Offset { get; init; }
    public bool AiConfigured { get; init; }
}

/// <summary>Competencia: tiendas líderes en tus categorías, lo que venden y copiarlo con mercancía importada.</summary>
[Authorize]
public sealed class CompetitorsController : Controller
{
    private readonly CompetitorService _competitors;
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly IBackgroundJobClient _jobs;
    private readonly ClaudeClient _claude;

    public CompetitorsController(CompetitorService competitors, AppDbContext context, IOrganizationContext organizationContext, IBackgroundJobClient jobs, ClaudeClient claude)
    {
        _competitors = competitors;
        _context = context;
        _organizationContext = organizationContext;
        _jobs = jobs;
        _claude = claude;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var stores = await _context.CompetitorStores.AsNoTracking().Where(c => c.OrganizationId == org && !c.Hidden).ToListAsync(cancellationToken);
        return View(new CompetitorsIndexViewModel
        {
            Leaders = stores.Where(s => s.TopAppearances > 0).OrderByDescending(s => s.TopAppearances).ThenBy(s => s.BestPosition ?? 99).ToList(),
            Followed = stores.Where(s => s.Followed && s.TopAppearances == 0).OrderBy(s => s.Nickname).ToList(),
            LastScan = stores.Max(s => s.LastScannedAt),
            WatchedCategories = await _context.WatchedCategories.CountAsync(c => c.OrganizationId == org && c.IsSelected, cancellationToken),
            AiConfigured = _claude.IsConfigured
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Scan(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        _jobs.Enqueue<MarketplaceJobsService>(job => job.ScanCompetitors(org));
        TempData["Success"] = "Buscando a los líderes en los más vendidos de tus categorías. Tarda 1 o 2 minutos; recarga la pantalla.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Follow(string? store, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(store))
        {
            TempData["Error"] = "Pega el link de una publicación de esa tienda o su nombre de usuario.";
            return RedirectToAction(nameof(Index));
        }
        var (found, error) = await _competitors.FollowAsync(org, store, cancellationToken);
        if (found is null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }
        TempData["Success"] = $"Siguiendo a {(string.IsNullOrEmpty(found.Nickname) ? "la tienda " + found.SellerId : found.Nickname)}.";
        return RedirectToAction(nameof(Store), new { id = found.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Hide(long id, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var store = await _context.CompetitorStores.FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == org, cancellationToken);
        if (store is not null)
        {
            store.Hidden = true;
            store.Followed = false;
            await _context.SaveChangesAsync(cancellationToken);
            TempData["Success"] = $"{store.Nickname} ya no aparece en la lista.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Store(long id, string? q, int offset = 0, CancellationToken cancellationToken = default)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var store = await _context.CompetitorStores.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == org, cancellationToken);
        if (store is null)
            return NotFound();
        var (items, total, error) = await _competitors.GetItemsAsync(org, store, q, offset, cancellationToken);
        return View(new CompetitorStoreViewModel { Store = store, Items = items, Total = total, Error = error, Query = q, Offset = offset, AiConfigured = _claude.IsConfigured });
    }

    /// <summary>El agente revisa los productos de la tienda y busca en eBay los que convienen.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Copy(long id, string? q, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var store = await _context.CompetitorStores.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == org, cancellationToken);
        if (store is null)
            return NotFound();
        if (!_claude.IsConfigured)
        {
            TempData["Error"] = "Copiar con el agente necesita IA: agrega Anthropic__ApiKey en Render.";
            return RedirectToAction(nameof(Store), new { id });
        }
        var request = $"Copia lo que conviene de la tienda {store.Nickname} (vendedor_id {store.SellerId})" +
                      (string.IsNullOrWhiteSpace(q) ? "" : $", solo productos de «{q.Trim()}»") +
                      ": revisa lo que más vende, busca la misma pieza en eBay e importa lo que deje ganancia.";
        _jobs.Enqueue<MarketplaceJobsService>(job => job.RunImportAgentRequest(org, request));
        TempData["Success"] = $"El agente está revisando lo que vende {store.Nickname} y buscándolo en eBay. Te aviso por Telegram; lo verás en Agente de importación.";
        return RedirectToAction(nameof(Store), new { id, q });
    }

    /// <summary>Encargo de un solo producto de la competencia.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CopyItem(long id, string title, decimal? price, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var store = await _context.CompetitorStores.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == org, cancellationToken);
        if (store is null)
            return NotFound();
        if (!_claude.IsConfigured)
        {
            TempData["Error"] = "Necesita IA: agrega Anthropic__ApiKey en Render.";
            return RedirectToAction(nameof(Store), new { id });
        }
        var t = title.Length > 200 ? title[..200] : title;
        var priceText = price is > 0 ? $" a {price.Value.ToString("C0", CultureInfo.GetCultureInfo("es-MX"))}" : "";
        var request = $"Busca en eBay la misma pieza que «{t}» (la vende {store.Nickname}{priceText} en Mercado Libre) e impórtala si deja ganancia.";
        _jobs.Enqueue<MarketplaceJobsService>(job => job.RunImportAgentRequest(org, request));
        TempData["Success"] = "Encargado al agente: busca esa pieza en eBay. Te aviso por Telegram.";
        return Redirect(Url.Action(nameof(Store), new { id }) + "#items");
    }
}
