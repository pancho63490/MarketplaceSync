using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Questions;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

public sealed class QuestionRow
{
    public required BuyerQuestion Question { get; init; }
    public string? ProductTitle { get; init; }
    public string? ImageUrl { get; init; }
    public string? Permalink { get; init; }
}

public sealed class QuestionsIndexViewModel
{
    public string Tab { get; init; } = "pending";
    public IReadOnlyList<QuestionRow> Rows { get; init; } = Array.Empty<QuestionRow>();
    public int PendingCount { get; init; }
    public int AnsweredCount { get; init; }
    public bool AiConfigured { get; init; }
    public bool HasAccounts { get; init; }
    public bool AutoAnswerEnabled { get; init; }
}

/// <summary>Preguntas de compradores en Mercado Libre, con respuesta sugerida por IA.</summary>
[Authorize]
public sealed class QuestionsController : Controller
{
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly IMercadoLibreQuestionService _questions;
    private readonly ClaudeClient _claude;
    private readonly ILogger<QuestionsController> _logger;

    public QuestionsController(
        AppDbContext context,
        IOrganizationContext organizationContext,
        IMercadoLibreQuestionService questions,
        ClaudeClient claude,
        ILogger<QuestionsController> logger)
    {
        _context = context;
        _organizationContext = organizationContext;
        _questions = questions;
        _claude = claude;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? tab, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var answered = string.Equals(tab, "answered", StringComparison.OrdinalIgnoreCase);

        var query = _context.BuyerQuestions.AsNoTracking().Where(q => q.OrganizationId == organizationId);
        var pendingCount = await query.CountAsync(q => q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);
        var answeredCount = await query.CountAsync(q => q.Status == BuyerQuestionStatus.Answered, cancellationToken);

        var questions = answered
            ? await query.Where(q => q.Status == BuyerQuestionStatus.Answered)
                .OrderByDescending(q => q.AnsweredAt).Take(100).ToListAsync(cancellationToken)
            : await query.Where(q => q.Status == BuyerQuestionStatus.Unanswered)
                .OrderBy(q => q.DateCreated).Take(100).ToListAsync(cancellationToken);

        var itemIds = questions.Select(q => q.ItemId).Distinct().ToList();
        var publications = await _context.MarketplacePublications
            .AsNoTracking()
            .Where(p => p.ExternalItemId != null && itemIds.Contains(p.ExternalItemId) && p.Product.OrganizationId == organizationId)
            .Select(p => new { p.ExternalItemId, p.Product.Title, p.Product.ImageUrl, p.Permalink })
            .ToListAsync(cancellationToken);
        var byItem = publications.GroupBy(p => p.ExternalItemId!).ToDictionary(g => g.Key, g => g.First());

        var hasAccounts = await _context.MarketplaceAccounts.AnyAsync(
            a => a.OrganizationId == organizationId && a.Marketplace == "MercadoLibre" && a.Status == "Connected", cancellationToken);

        return View(new QuestionsIndexViewModel
        {
            Tab = answered ? "answered" : "pending",
            PendingCount = pendingCount,
            AnsweredCount = answeredCount,
            AiConfigured = _claude.IsConfigured,
            HasAccounts = hasAccounts,
            AutoAnswerEnabled = await _context.AutomationSettings.AnyAsync(a => a.OrganizationId == organizationId && a.AutoAnswerEnabled, cancellationToken),
            Rows = questions.Select(q =>
            {
                byItem.TryGetValue(q.ItemId, out var publication);
                return new QuestionRow
                {
                    Question = q,
                    ProductTitle = publication?.Title,
                    ImageUrl = publication?.ImageUrl,
                    Permalink = publication?.Permalink
                };
            }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var result = await _questions.SyncOrganizationAsync(organizationId, cancellationToken);
            TempData["Success"] = result.Unanswered == 0
                ? "Al día: no tienes preguntas sin responder."
                : $"{result.Unanswered} {(result.Unanswered == 1 ? "pregunta" : "preguntas")} por responder" +
                  (result.New > 0 ? $" ({result.New} nuevas)." : ".");
            if (result.Warnings.Count > 0)
                TempData["Warning"] = string.Join(" ", result.Warnings);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Question sync failed.");
            TempData["Error"] = "No fue posible leer las preguntas de Mercado Libre. Intenta de nuevo en un momento.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Answer(long id, string? text, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            await _questions.AnswerAsync(organizationId, id, text ?? string.Empty, cancellationToken);
            TempData["Success"] = "Respuesta enviada a Mercado Libre.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            TempData["Error"] = exception.Message;
            TempData[$"Draft:{id}"] = text;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Answer failed for question {QuestionId}.", id);
            TempData["Error"] = "No se pudo enviar la respuesta. Intenta de nuevo.";
            TempData[$"Draft:{id}"] = text;
        }
        return Redirect(Url.Action(nameof(Index)) + $"#q{id}");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suggest(long id, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (!_claude.IsConfigured)
        {
            TempData["Warning"] = "La IA no está configurada. Agrega Anthropic__ApiKey en Render para recibir respuestas sugeridas.";
            return RedirectToAction(nameof(Index));
        }

        var suggestion = await _questions.SuggestAsync(organizationId, id, cancellationToken);
        if (suggestion is null)
            TempData["Error"] = "La IA no pudo preparar una respuesta. Intenta de nuevo o escríbela tú.";
        return Redirect(Url.Action(nameof(Index)) + $"#q{id}");
    }
}
