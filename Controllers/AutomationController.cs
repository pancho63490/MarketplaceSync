using MarketplaceSync.Services.Automation;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Automation;
using MarketplaceSync.Web.Models.Questions;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

public sealed class AutomationViewModel
{
    public required AutomationSettings Settings { get; init; }
    public required HashSet<string> Topics { get; init; }
    public int AutoAnsweredWeek { get; init; }
    public int ToSellerWeek { get; init; }
    public IReadOnlyList<BuyerQuestion> RecentAuto { get; init; } = Array.Empty<BuyerQuestion>();
    public int ThanksSentWeek { get; init; }
    public int ThanksFailedWeek { get; init; }
    public bool AiConfigured { get; init; }
}

/// <summary>Configuración → Automático: lo que la app hace sola.</summary>
[Authorize]
public sealed class AutomationController : Controller
{
    private readonly AppDbContext _context;
    private readonly AutomationService _automation;
    private readonly IOrganizationContext _organizationContext;
    private readonly MarketplaceSync.Services.Ai.ClaudeClient _claude;

    public AutomationController(AppDbContext context, AutomationService automation, IOrganizationContext organizationContext,
        MarketplaceSync.Services.Ai.ClaudeClient claude)
    {
        _context = context;
        _automation = automation;
        _organizationContext = organizationContext;
        _claude = claude;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _automation.GetAsync(org, cancellationToken);
        var week = DateTime.UtcNow.AddDays(-7);
        return View(new AutomationViewModel
        {
            Settings = settings,
            Topics = AutomationService.EnabledTopics(settings),
            AutoAnsweredWeek = await _context.BuyerQuestions.CountAsync(q => q.OrganizationId == org && q.AutoAnswered && q.AnsweredAt >= week, cancellationToken),
            ToSellerWeek = await _context.BuyerQuestions.CountAsync(q => q.OrganizationId == org && !q.AutoAnswered && q.DateCreated >= week, cancellationToken),
            RecentAuto = await _context.BuyerQuestions.AsNoTracking()
                .Where(q => q.OrganizationId == org && q.AutoAnswered)
                .OrderByDescending(q => q.AnsweredAt).Take(5).ToListAsync(cancellationToken),
            ThanksSentWeek = await _context.SalesOrders.CountAsync(o => o.OrganizationId == org && o.ThankYouStatus == "sent" && o.ThankYouSentAt >= week, cancellationToken),
            ThanksFailedWeek = await _context.SalesOrders.CountAsync(o => o.OrganizationId == org && o.ThankYouStatus == "failed" && o.ThankYouSentAt >= week, cancellationToken),
            AiConfigured = _claude.IsConfigured
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveQuestions(bool enabled, string[]? topics, string? invoice, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _automation.GetAsync(org, cancellationToken);
        if (enabled && !settings.AutoAnswerEnabled)
            settings.AutoAnswerSince = DateTime.UtcNow;
        settings.AutoAnswerEnabled = enabled;
        var allowed = AutomationService.Topics.Where(t => t.CanAuto).Select(t => t.Id).ToHashSet();
        settings.AutoAnswerTopics = string.Join(',', (topics ?? Array.Empty<string>()).Where(allowed.Contains).Distinct());
        settings.OffersInvoice = invoice switch { "yes" => true, "no" => false, _ => null };
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["Success"] = !enabled ? "Respuestas automáticas apagadas: todas las preguntas pasan por ti."
            : settings.AutoAnswerTopics.Length == 0 ? "Prendidas, pero sin temas elegidos: ninguna se responderá sola."
            : _claude.IsConfigured ? "Listo: las preguntas nuevas de esos temas se responden solas."
            : "Guardado. Para responder solas hace falta la IA (Anthropic__ApiKey en Render).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTelegramAutomations(
        string? repricingMode,
        decimal? repricingMaxIncrease,
        bool labelsDaily,
        int? labelsHour,
        bool labelsThermal,
        bool weeklyPurchase,
        int? weeklyPurchaseDay,
        bool weeklyPurchaseIncludeNew,
        CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _automation.GetAsync(org, cancellationToken);
        settings.RepricingMode = repricingMode is RepricingModes.Off or RepricingModes.Auto ? repricingMode : RepricingModes.Alert;
        settings.RepricingMaxIncreasePercent = Math.Clamp(repricingMaxIncrease ?? 25m, 1m, 100m);
        settings.LabelsDaily = labelsDaily;
        settings.LabelsHour = Math.Clamp(labelsHour ?? 9, 5, 18);
        settings.LabelsThermal = labelsThermal;
        settings.WeeklyPurchaseEnabled = weeklyPurchase;
        settings.WeeklyPurchaseDay = Math.Clamp(weeklyPurchaseDay ?? 1, 0, 6);
        settings.WeeklyPurchaseIncludeNew = weeklyPurchaseIncludeNew;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "Guardado. Los avisos llegan al Telegram vinculado.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePhotos(bool enabled, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _automation.GetAsync(org, cancellationToken);
        settings.FixPhotosOnPublish = enabled;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["Success"] = enabled ? "Listo: los productos nuevos se publican con la foto en fondo blanco." : "Fondo blanco automático apagado.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveThanks(bool enabled, string? message, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _automation.GetAsync(org, cancellationToken);
        var text = (message ?? string.Empty).Trim();
        if (enabled && text.Length < 10)
        {
            TempData["Error"] = "Escribe el mensaje de gracias.";
            return RedirectToAction(nameof(Index));
        }
        if (text.Length > 350)
        {
            TempData["Error"] = $"El mensaje tiene {text.Length} caracteres; Mercado Libre acepta hasta 350.";
            return RedirectToAction(nameof(Index));
        }
        var (_, removed) = HttpContext.RequestServices.GetRequiredService<MarketplaceSync.Services.MercadoLibre.IMercadoLibreQuestionService>().Sanitize(text);
        if (removed)
        {
            TempData["Error"] = "Quita teléfonos, correos, links o redes del mensaje: Mercado Libre lo bloquea.";
            return RedirectToAction(nameof(Index));
        }
        if (enabled && !settings.ThankYouEnabled)
            settings.ThankYouSince = DateTime.UtcNow;
        settings.ThankYouEnabled = enabled;
        if (text.Length > 0) settings.ThankYouMessage = text;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["Success"] = enabled ? "Listo: cada venta nueva recibe el mensaje de gracias." : "Mensaje de gracias apagado.";
        return RedirectToAction(nameof(Index));
    }
}
