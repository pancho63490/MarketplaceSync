using Hangfire;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.Jobs;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Agents;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

public sealed class AgentIndexViewModel
{
    public bool Enabled { get; init; }
    public int Weekly { get; init; }
    public int ImportedThisWeek { get; init; }
    public int Candidates { get; init; }
    public int WatchedCategories { get; init; }
    public bool AiConfigured { get; init; }
    public bool Running { get; init; }
    public IReadOnlyList<AgentRun> Runs { get; init; } = Array.Empty<AgentRun>();
}

/// <summary>Agente de importación: configuración, ejecutar ahora y lo que decidió en cada corrida.</summary>
[Authorize]
public sealed class AgentController : Controller
{
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly ICrossBorderPricingService _pricing;
    private readonly IBackgroundJobClient _jobs;
    private readonly ClaudeClient _claude;

    public AgentController(AppDbContext context, IOrganizationContext organizationContext, ICrossBorderPricingService pricing, IBackgroundJobClient jobs, ClaudeClient claude)
    {
        _context = context;
        _organizationContext = organizationContext;
        _pricing = pricing;
        _jobs = jobs;
        _claude = claude;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var runs = await _context.AgentRuns.AsNoTracking()
            .Include(r => r.Steps)
            .Where(r => r.OrganizationId == org)
            .OrderByDescending(r => r.StartedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
        return View(new AgentIndexViewModel
        {
            Enabled = settings.ImportAgentEnabled,
            Weekly = settings.AutoPrepareWeekly,
            ImportedThisWeek = await _context.PurchaseCandidates.CountAsync(x => x.OrganizationId == org && x.PreparedAt >= weekAgo, cancellationToken),
            Candidates = await _context.PurchaseCandidates.CountAsync(x => x.OrganizationId == org && x.Status == PurchaseCandidateStatus.Suggested && x.ProductId == null, cancellationToken),
            WatchedCategories = await _context.WatchedCategories.CountAsync(x => x.OrganizationId == org && x.IsSelected, cancellationToken),
            AiConfigured = _claude.IsConfigured,
            Running = runs.Any(r => r.Status == AgentRunStatus.Running && r.StartedAt > DateTime.UtcNow.AddMinutes(-30)),
            Runs = runs
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(bool enabled, int weekly, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        settings.ImportAgentEnabled = enabled;
        settings.AutoPrepareWeekly = Math.Clamp(weekly, 0, 50);
        await _pricing.SaveSettingsAsync(settings, cancellationToken);
        TempData["Success"] = enabled
            ? $"Listo: el agente revisará el Radar cada mañana e importará hasta {settings.AutoPrepareWeekly} productos por semana."
            : "Agente apagado. Puedes correrlo a mano cuando quieras.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var candidates = await _context.PurchaseCandidates.CountAsync(x => x.OrganizationId == org && x.Status == PurchaseCandidateStatus.Suggested && x.ProductId == null, cancellationToken);
        if (candidates > 0)
        {
            _jobs.Enqueue<MarketplaceJobsService>(job => job.RunImportAgent(org));
            TempData["Success"] = "El agente está revisando el Radar. Tarda unos minutos; te aviso por Telegram al terminar (o recarga esta pantalla).";
            return RedirectToAction(nameof(Index));
        }

        if (!await _context.WatchedCategories.AnyAsync(x => x.OrganizationId == org && x.IsSelected, cancellationToken))
        {
            TempData["Warning"] = "Primero elige qué categorías vigilar (por ejemplo Refacciones Autos y Camionetas): de ahí salen los productos que revisa el agente.";
            return RedirectToAction("Index", "WatchedCategories");
        }

        // Sin candidatos: explora las categorías, busca precios en las tiendas y al final corre el agente.
        _jobs.Enqueue<MarketplaceJobsService>(job => job.DiscoverForOrganization(org));
        TempData["Success"] = "Buscando productos: exploro tus categorías, busco precios en las tiendas y luego el agente revisa lo que encuentre. Tarda unos 10 a 20 minutos; te aviso por Telegram.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestAi(CancellationToken cancellationToken)
    {
        var (ok, message) = await _claude.TestAsync(cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Encargo(string? request, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        request = request?.Trim();
        if (string.IsNullOrWhiteSpace(request) || request.Length < 3)
        {
            TempData["Error"] = "Escribe qué quieres que busque, por ejemplo “piezas de suspensión para Ford Ranger 2019” o “Monster Energy”.";
            return RedirectToAction(nameof(Index));
        }
        if (!_claude.IsConfigured)
        {
            TempData["Error"] = "Los encargos necesitan IA: agrega Anthropic__ApiKey en Render.";
            return RedirectToAction(nameof(Index));
        }
        if (request.Length > 500) request = request[..500];
        _jobs.Enqueue<MarketplaceJobsService>(job => job.RunImportAgentRequest(org, request));
        TempData["Success"] = $"Encargo recibido: «{request}». El agente busca en Mercado Libre y eBay; tarda unos minutos y te aviso por Telegram.";
        return RedirectToAction(nameof(Index));
    }
}
