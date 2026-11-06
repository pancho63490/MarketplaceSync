using Hangfire;
using MarketplaceSync.Services.Photos;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

public sealed record PhotoRow(int ProductId, string Title, string? ImageUrl, string? OriginalImageUrl, string? Check, int? Width, int? Height, bool Published);

public sealed class PhotosIndexViewModel
{
    public string Filter { get; init; } = "todo";
    public IReadOnlyList<PhotoRow> Rows { get; init; } = Array.Empty<PhotoRow>();
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
    public int Unchecked { get; init; }
    public bool AiConfigured { get; init; }
    public bool ToolAvailable { get; init; }
}

/// <summary>Fotos: portadas con fondo blanco como pide Mercado Libre.</summary>
[Authorize]
public sealed class PhotosController : Controller
{
    private readonly AppDbContext _context;
    private readonly PhotoStudioService _photos;
    private readonly IOrganizationContext _organizationContext;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<PhotosController> _logger;

    public PhotosController(AppDbContext context, PhotoStudioService photos, IOrganizationContext organizationContext,
        IBackgroundJobClient jobs, ILogger<PhotosController> logger)
    {
        _context = context;
        _photos = photos;
        _organizationContext = organizationContext;
        _jobs = jobs;
        _logger = logger;
    }

    private static readonly string[] Fixable = { "fill", "plain", "busy", "small" };

    [HttpGet]
    public async Task<IActionResult> Index(string? filter, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var all = await _context.Products.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.ImageUrl != null && p.ImageUrl != "" && p.Status != "Archived")
            .OrderByDescending(p => p.MarketplacePublications.Any(m => m.IsPublished))
            .ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Select(p => new PhotoRow(p.Id, p.Title ?? "Producto sin título", p.ImageUrl, p.OriginalImageUrl, p.PhotoCheck, p.PhotoWidth, p.PhotoHeight,
                p.MarketplacePublications.Any(m => m.IsPublished)))
            .ToListAsync(cancellationToken);

        var counts = new Dictionary<string, int>
        {
            ["arreglar"] = all.Count(r => r.Check is not null && (Fixable.Contains(r.Check) || r.Check == "error")),
            ["listas"] = all.Count(r => r.Check == "ok"),
            ["arregladas"] = all.Count(r => r.OriginalImageUrl != null),
            ["todo"] = all.Count
        };
        filter = filter is "listas" or "arregladas" or "todo" ? filter : counts["arreglar"] > 0 ? "arreglar" : "todo";
        var rows = filter switch
        {
            "arreglar" => all.Where(r => (r.Check is not null && Fixable.Contains(r.Check)) || r.Check == "error"),
            "listas" => all.Where(r => r.Check == "ok"),
            "arregladas" => all.Where(r => r.OriginalImageUrl != null),
            _ => all
        };
        return View(new PhotosIndexViewModel
        {
            Filter = filter,
            Rows = rows.Take(200).ToList(),
            Counts = counts,
            Unchecked = all.Count(r => r.Check is null),
            AiConfigured = _photos.AiConfigured,
            ToolAvailable = _photos.ToolAvailable
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckAll(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        // Pocas: aquí mismo. Muchas: en segundo plano.
        var pending = await _context.Products.CountAsync(p => p.OrganizationId == org && p.ImageUrl != null && p.ImageUrl != "" && p.PhotoCheckedAt == null, cancellationToken);
        if (pending <= 15)
        {
            var n = await _photos.CheckPendingAsync(org, 15, cancellationToken);
            TempData["Success"] = n == 0 ? "Todas las fotos ya estaban revisadas." : $"Revisé {n} {(n == 1 ? "foto" : "fotos")}.";
        }
        else
        {
            _jobs.Enqueue<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(j => j.CheckPhotos(org));
            TempData["Success"] = $"Revisando {pending} fotos en segundo plano. Recarga en un par de minutos.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Recheck(int id, string? returnTo, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var product = await _context.Products.FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == org, cancellationToken);
        if (product is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(product.ImageUrl))
        {
            var check = await _photos.CheckAsync(product, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            TempData["Success"] = "Foto revisada: " + PhotoStudioService.Describe(check).Label.ToLowerInvariant() + ".";
        }
        return Back(returnTo, id);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fix(int id, bool updateListings, string? returnTo, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var result = await _photos.FixAsync(org, id, updateListings, cancellationToken);
            if (result.Error is null) TempData["Success"] = result.Message;
            else TempData["Error"] = result.Error;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Photo fix failed for product {Product}.", id);
            TempData["Error"] = "No se pudo arreglar la foto. Intenta de nuevo o cámbiala por otra.";
        }
        return Back(returnTo, id);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Undo(int id, bool updateListings, string? returnTo, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var result = await _photos.UndoAsync(org, id, updateListings, cancellationToken);
            if (result.Error is null) TempData["Success"] = result.Message;
            else TempData["Error"] = result.Error;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        return Back(returnTo, id);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FixAll(bool updateListings, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        _jobs.Enqueue<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(j => j.FixPhotos(org, updateListings));
        TempData["Success"] = "Arreglando las fotos en segundo plano (hasta 40 por vez). Recarga en unos minutos para ver el resultado.";
        return RedirectToAction(nameof(Index), new { filter = "arregladas" });
    }

    private IActionResult Back(string? returnTo, int id) =>
        returnTo == "product" ? RedirectToAction("Details", "Products", new { id }) : RedirectToAction(nameof(Index), new { filter = Request.Query["filter"].FirstOrDefault() });
}
