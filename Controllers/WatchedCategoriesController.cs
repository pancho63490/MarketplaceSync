using Hangfire;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MarketInsights;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.MarketInsights;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Services;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

public sealed class WatchedCategoryRow
{
    public required WatchedCategory Category { get; init; }
    public int Leaves { get; init; }
    public int LeavesScanned { get; init; }
    public int LeavesWithRanking { get; init; }
    public DateTime? LastScannedAt { get; init; }
    public int ToBuy { get; init; }
}

public sealed class WatchedCategoriesViewModel
{
    public IReadOnlyList<WatchedCategoryRow> Rows { get; init; } = Array.Empty<WatchedCategoryRow>();
    public IReadOnlyList<(string Id, string Name, string? Path)> Suggestions { get; init; } = Array.Empty<(string, string, string?)>();
    public string? Query { get; init; }
    public string? SuggestionsError { get; init; }
    public int AutoPrepareWeekly { get; init; }
    public bool PublishOnReceipt { get; init; }
    public int PreparedThisWeek { get; init; }
    public int TotalLeaves { get; init; }
    public bool HasAccount { get; init; }
    public IReadOnlySet<string> WatchedIds { get; init; } = new HashSet<string>();
}

/// <summary>Elige las categorías que la app explora sola para encontrar qué comprar.</summary>
[Authorize]
public sealed class WatchedCategoriesController : Controller
{
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly CategoryDiscoveryService _discovery;
    private readonly MercadoLibreCategoryService _categorySearch;
    private readonly ICrossBorderPricingService _pricing;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<WatchedCategoriesController> _logger;

    public WatchedCategoriesController(
        AppDbContext context,
        IOrganizationContext organizationContext,
        CategoryDiscoveryService discovery,
        MercadoLibreCategoryService categorySearch,
        ICrossBorderPricingService pricing,
        IBackgroundJobClient jobs,
        ILogger<WatchedCategoriesController> logger)
    {
        _context = context;
        _organizationContext = organizationContext;
        _discovery = discovery;
        _categorySearch = categorySearch;
        _pricing = pricing;
        _jobs = jobs;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? q, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);

        var all = await _context.WatchedCategories
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var roots = all.Where(x => x.IsSelected).OrderBy(x => x.PathText ?? x.Name).ToList();

        var toBuyByCategory = await _context.PurchaseCandidates
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == PurchaseCandidateStatus.Suggested && x.DemandScore > 0)
            .GroupBy(x => x.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key ?? "", x => x.Count, cancellationToken);

        var rows = roots.Select(root =>
        {
            var leaves = all.Where(x => x.ParentWatchedCategoryId == root.Id).ToList();
            if (root.IsLeaf) leaves.Add(root);
            var ids = leaves.Select(x => x.CategoryId).Append(root.CategoryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new WatchedCategoryRow
            {
                Category = root,
                Leaves = leaves.Count,
                LeavesScanned = leaves.Count(x => x.LastScannedAt != null),
                LeavesWithRanking = leaves.Count(x => x.LastItemsFound > 0),
                LastScannedAt = leaves.Max(x => x.LastScannedAt),
                ToBuy = toBuyByCategory.Where(kv => ids.Contains(kv.Key)).Sum(kv => kv.Value)
            };
        }).ToList();

        // Sugerencias: búsqueda por texto o, sin texto, las secciones de Accesorios para Vehículos.
        var suggestions = new List<(string Id, string Name, string? Path)>();
        string? suggestionsError = null;
        var query = q?.Trim();
        try
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                var found = await _categorySearch.SuggestCategoriesAsync(query);
                suggestions.AddRange(found
                    .Where(x => !string.IsNullOrWhiteSpace(x.Category_Id))
                    .DistinctBy(x => x.Category_Id)
                    .Select(x => (x.Category_Id!, x.Category_Name ?? x.Category_Id!, x.Domain_Name)));
            }
            else
            {
                var root = await _discovery.GetCategoryAsync(CategoryDiscoveryService.VehicleAccessoriesRootId, cancellationToken);
                suggestions.AddRange(root.Children.Select(c => (c.Id, c.Name, (string?)root.Name)));
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not load category suggestions.");
            suggestionsError = "No se pudieron cargar las categorías de Mercado Libre en este momento.";
        }

        var weekAgo = DateTime.UtcNow.AddDays(-7);
        return View(new WatchedCategoriesViewModel
        {
            Rows = rows,
            Suggestions = suggestions,
            Query = query,
            SuggestionsError = suggestionsError,
            AutoPrepareWeekly = settings.AutoPrepareWeekly,
            PublishOnReceipt = settings.PublishOnReceipt,
            PreparedThisWeek = await _context.PurchaseCandidates.CountAsync(
                x => x.OrganizationId == organizationId && x.PreparedAt >= weekAgo, cancellationToken),
            TotalLeaves = all.Count(x => x.IsLeaf && x.Enabled),
            HasAccount = await _context.MarketplaceAccounts.AnyAsync(
                x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre" && x.Status == "Connected", cancellationToken),
            WatchedIds = all.Where(x => x.IsSelected).Select(x => x.CategoryId).ToHashSet(StringComparer.OrdinalIgnoreCase)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string categoryId, string? q, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(categoryId) || !System.Text.RegularExpressions.Regex.IsMatch(categoryId.Trim(), @"^MLM\d{1,12}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            TempData["Error"] = "Esa categoría no es válida.";
            return RedirectToAction(nameof(Index), new { q });
        }

        try
        {
            var added = await _discovery.AddAsync(organizationId, categoryId, cancellationToken);
            var leaves = await _context.WatchedCategories.CountAsync(x => x.ParentWatchedCategoryId == added.Id, cancellationToken);
            TempData["Success"] = leaves > 0
                ? $"Vigilando {added.Name} y {leaves} subcategorías. La app las revisa cada día."
                : $"Vigilando {added.Name}. La app la revisa cada día.";
            _jobs.Enqueue<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(job => job.DiscoverForOrganization(organizationId));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not add watched category {CategoryId}.", categoryId);
            TempData["Error"] = "No se pudo agregar la categoría. Intenta de nuevo en un momento.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(int id, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            await _discovery.RemoveAsync(organizationId, id, cancellationToken);
            TempData["Success"] = "Categoría quitada. Lo que ya encontró se queda en Qué comprar.";
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id, bool enabled, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            await _discovery.SetEnabledAsync(organizationId, id, enabled, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        TempData["Success"] = enabled ? "Categoría activada." : "Categoría en pausa: no se explorará hasta que la actives.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(int autoPrepareWeekly, bool publishOnReceipt, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _context.CrossBorderSettings.FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (settings is null)
        {
            await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
            settings = await _context.CrossBorderSettings.FirstAsync(x => x.OrganizationId == organizationId, cancellationToken);
        }
        settings.AutoPrepareWeekly = Math.Clamp(autoPrepareWeekly, 0, 50);
        settings.PublishOnReceipt = publishOnReceipt;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["Success"] = settings.AutoPrepareWeekly == 0
            ? "Guardado. La app no preparará productos sola; los verás en Qué comprar."
            : $"Guardado. Cada semana la app dejará listos hasta {settings.AutoPrepareWeekly} productos.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExploreNow(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        _jobs.Enqueue<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(job => job.DiscoverForOrganization(organizationId));
        TempData["Success"] = "Explorando. En unos minutos verás los resultados en Qué comprar.";
        return RedirectToAction(nameof(Index));
    }
}
