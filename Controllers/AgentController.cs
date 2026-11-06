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
        _jobs.Enqueue<MarketplaceJobsService>(job => job.RunImportAgent(org));
        TempData["Success"] = "El agente está revisando el Radar. Tarda unos minutos; te aviso por Telegram al terminar (o recarga esta pantalla).";
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
}
