using System.Text.Json;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Revisar títulos publicados: palabras prohibidas, inglés, términos de México; sugerir y cambiar.</summary>
[Authorize]
public sealed class TitlesController : Controller
{
    private const int MaxAiPerClick = 15;
    private const int MaxApplyPerClick = 30;

    private readonly IOrganizationContext _organizationContext;
    private readonly AppDbContext _context;
    private readonly TitleQuality _titles;
    private readonly MercadoLibreItemEditorService _editor;

    public TitlesController(IOrganizationContext organizationContext, AppDbContext context, TitleQuality titles, MercadoLibreItemEditorService editor)
    {
        _organizationContext = organizationContext;
        _context = context;
        _titles = titles;
        _editor = editor;
    }

    public sealed record TitleRow(int PublicationId, string ItemId, string Title, string Status, bool CanEdit, List<TitleIssue> Issues, string Suggestion, bool FromAi);

    public sealed class TitlesViewModel
    {
        public List<TitleRow> Rows { get; init; } = new();
        public string Filter { get; init; } = "problems";
        public int Total { get; init; }
        public int Blocking { get; init; }
        public int Warnings { get; init; }
        public bool AiConfigured { get; init; }
    }

    public sealed class ApplyRow
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public bool Selected { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? filter, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null &&
                        (p.Status == "active" || p.Status == "paused"))
            .Select(p => new { p.Id, p.ExternalItemId, p.Product.Title, p.Status, p.SoldQuantity })
            .ToListAsync(cancellationToken);
        var ai = ReadAiSuggestions();

        var all = publications.Select(p =>
        {
            var issues = TitleQuality.Review(p.Title);
            var fromAi = ai.TryGetValue(p.Id, out var aiTitle);
            var suggestion = fromAi ? aiTitle! : issues.Count > 0 ? TitleQuality.Clean(p.Title) : p.Title;
            return new TitleRow(p.Id, p.ExternalItemId!, p.Title ?? string.Empty, p.Status ?? string.Empty, (p.SoldQuantity ?? 0) == 0, issues, suggestion, fromAi);
        }).ToList();

        var f = filter is "all" or "blocking" ? filter : "problems";
        var rows = f switch
        {
            "all" => all,
            "blocking" => all.Where(r => r.Issues.Any(i => i.Blocks)).ToList(),
            _ => all.Where(r => r.Issues.Count > 0).ToList()
        };
        return View(new TitlesViewModel
        {
            Rows = rows.OrderByDescending(r => r.Issues.Count(i => i.Blocks)).ThenByDescending(r => r.Issues.Count).ThenByDescending(r => r.CanEdit).ToList(),
            Filter = f,
            Total = all.Count,
            Blocking = all.Count(r => r.Issues.Any(i => i.Blocks)),
            Warnings = all.Count(r => r.Issues.Count > 0),
            AiConfigured = HttpContext.RequestServices.GetRequiredService<ClaudeClient>().IsConfigured
        });
    }

    /// <summary>Pide a la IA títulos mejores (una publicación o las primeras con problemas que se pueden cambiar).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suggest(int? id, string? filter, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var query = _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null &&
                        (p.Status == "active" || p.Status == "paused") && (p.SoldQuantity == null || p.SoldQuantity == 0));
        if (id is { } one) query = query.Where(p => p.Id == one);
        var candidates = await query.Select(p => new { p.Id, p.Product.Title, p.Product.Brand, p.Product.Model, p.Product.Description }).ToListAsync(cancellationToken);
        var pending = candidates.Where(c => id is not null || TitleQuality.Review(c.Title).Count > 0).Take(MaxAiPerClick).ToList();

        var ai = ReadAiSuggestions();
        var improved = 0;
        foreach (var c in pending)
        {
            var suggestion = await _titles.SuggestAsync(c.Title, c.Brand, c.Model, c.Description, cancellationToken);
            // Si la IA no mejoró lo que ya da la corrección automática, no se marca como IA.
            if (suggestion != TitleQuality.Clean(c.Title))
            {
                ai[c.Id] = suggestion;
                improved++;
            }
        }
        TempData["TitleAi"] = JsonSerializer.Serialize(ai);
        TempData[improved > 0 ? "Success" : "Warning"] = pending.Count == 0
            ? "No hay títulos con problemas que se puedan cambiar."
            : improved > 0
                ? $"Listo: {improved} {(improved == 1 ? "título sugerido" : "títulos sugeridos")} con IA. Revísalos antes de cambiarlos."
                : "La IA no encontró algo mejor que la corrección automática.";
        return RedirectToAction(nameof(Index), null, new { filter }, id is { } i ? $"t{i}" : null);
    }

    /// <summary>Cambia en Mercado Libre los títulos marcados (solo publicaciones sin ventas).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(List<ApplyRow>? rows, string? filter, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var selected = (rows ?? new()).Where(r => r.Selected && !string.IsNullOrWhiteSpace(r.Title)).Take(MaxApplyPerClick).ToList();
        if (selected.Count == 0)
        {
            TempData["Warning"] = "Marca los títulos que quieres cambiar.";
            return RedirectToAction(nameof(Index), new { filter });
        }

        var changed = 0;
        var errors = new List<string>();
        foreach (var row in selected)
        {
            var title = row.Title!.Trim();
            var blocking = TitleQuality.Review(title).Where(i => i.Blocks).ToList();
            if (blocking.Count > 0)
            {
                errors.Add($"«{Short(title)}»: {blocking[0].Message}");
                continue;
            }
            try
            {
                var result = await _editor.UpdateAsync(org, row.Id, new ItemEdit { Title = title }, cancellationToken);
                if (!result.Ok) { errors.Add($"«{Short(title)}»: {string.Join(" ", result.Errors)}"); continue; }
                var product = await _context.MarketplacePublications.Where(p => p.Id == row.Id && p.Product.OrganizationId == org)
                    .Select(p => p.Product).FirstOrDefaultAsync(cancellationToken);
                if (product is not null)
                {
                    product.Title = title;
                    await _context.SaveChangesAsync(cancellationToken);
                }
                changed++;
            }
            catch (MercadoLibreEditException exception)
            {
                errors.Add($"«{Short(title)}»: {exception.Message}");
            }
        }

        if (changed > 0)
            TempData["Success"] = $"Cambié {changed} {(changed == 1 ? "título" : "títulos")} en Mercado Libre.";
        if (errors.Count > 0)
            TempData[changed > 0 ? "Warning" : "Error"] = string.Join(" · ", errors.Take(4));
        return RedirectToAction(nameof(Index), new { filter });
    }

    private Dictionary<int, string> ReadAiSuggestions()
    {
        if (TempData.Peek("TitleAi") is string json)
        {
            try { return JsonSerializer.Deserialize<Dictionary<int, string>>(json) ?? new(); }
            catch (JsonException) { }
        }
        return new();
    }

    private static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;
}
