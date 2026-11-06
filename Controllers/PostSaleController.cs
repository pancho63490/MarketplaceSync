using MarketplaceSync.Services.PostSale;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.PostSale;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

public sealed class PostSaleIndexViewModel
{
    public string Tab { get; init; } = "messages";
    public IReadOnlyList<PendingConversation> Pending { get; init; } = Array.Empty<PendingConversation>();
    public IReadOnlyList<OrderClaim> OpenClaims { get; init; } = Array.Empty<OrderClaim>();
    public IReadOnlyList<OrderClaim> ClosedClaims { get; init; } = Array.Empty<OrderClaim>();
    public IReadOnlyDictionary<int, string> OrderTitles { get; init; } = new Dictionary<int, string>();
    public bool HasAccounts { get; init; }
}

public sealed class ClaimDetailViewModel
{
    public required OrderClaim Claim { get; init; }
    public IReadOnlyList<ClaimMessage> Messages { get; init; } = Array.Empty<ClaimMessage>();
    public string? Error { get; init; }
    public string? OrderTitle { get; init; }
}

/// <summary>Postventa: mensajes sin responder y reclamos, sin entrar a Mercado Libre.</summary>
[Authorize]
public sealed class PostSaleController : Controller
{
    private readonly PostSaleService _postSale;
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly ILogger<PostSaleController> _logger;

    public PostSaleController(PostSaleService postSale, AppDbContext context, IOrganizationContext organizationContext, ILogger<PostSaleController> logger)
    {
        _postSale = postSale;
        _context = context;
        _organizationContext = organizationContext;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? tab, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var pending = await _postSale.GetPendingAsync(org, 50, cancellationToken);
        var open = await _context.OrderClaims.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.Status == "opened")
            .OrderBy(c => c.DueDate ?? DateTime.MaxValue).ThenByDescending(c => c.LastUpdated)
            .ToListAsync(cancellationToken);
        var closed = await _context.OrderClaims.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.Status != "opened")
            .OrderByDescending(c => c.LastUpdated).Take(15)
            .ToListAsync(cancellationToken);
        var orderIds = open.Concat(closed).Where(c => c.SalesOrderId.HasValue).Select(c => c.SalesOrderId!.Value).Distinct().ToList();
        var titles = await _context.SalesOrderItems.AsNoTracking()
            .Where(i => orderIds.Contains(i.SalesOrderId))
            .GroupBy(i => i.SalesOrderId)
            .Select(g => new { g.Key, Title = g.Select(i => i.Title).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Key, x => x.Title ?? "", cancellationToken);

        return View(new PostSaleIndexViewModel
        {
            Tab = tab == "claims" || (tab is null && pending.Count == 0 && open.Count > 0) ? "claims" : "messages",
            Pending = pending,
            OpenClaims = open,
            ClosedClaims = closed,
            OrderTitles = titles,
            HasAccounts = await _context.MarketplaceAccounts.AnyAsync(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected", cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(string? tab, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var result = await _postSale.SyncAsync(org, cancellationToken);
        if (result.Errors.Count > 0)
            TempData["Warning"] = string.Join(" ", result.Errors);
        else
            TempData["Success"] = result.Messages == 0 && result.Claims == 0
                ? "Todo al día: no hay mensajes ni reclamos nuevos."
                : $"Listo: {result.Messages} {(result.Messages == 1 ? "mensaje nuevo" : "mensajes nuevos")} y {result.Claims} {(result.Claims == 1 ? "reclamo revisado" : "reclamos revisados")}.";
        return RedirectToAction(nameof(Index), new { tab });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(int orderId, string? text, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var error = await _postSale.SendMessageAsync(org, orderId, text ?? string.Empty, cancellationToken);
            if (error is null)
                TempData["Success"] = "Mensaje enviado al comprador.";
            else
            {
                TempData["Error"] = error;
                TempData["DraftMessage"] = text;
            }
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Sending message failed for order {Order}.", orderId);
            TempData["Error"] = "Mercado Libre no respondió; tu mensaje no se envió. Intenta de nuevo.";
            TempData["DraftMessage"] = text;
        }
        return Redirect(Url.Action("Details", "Orders", new { id = orderId }) + "#conversacion");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suggest(int orderId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var text = await _postSale.SuggestReplyAsync(org, orderId, cancellationToken);
        return Json(new { ok = !string.IsNullOrWhiteSpace(text), text });
    }

    [HttpGet]
    public async Task<IActionResult> Claim(long id, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (claim, messages, error) = await _postSale.GetClaimAsync(org, id, cancellationToken);
        if (claim is null)
            return NotFound();
        var title = claim.SalesOrderId is { } orderId
            ? await _context.SalesOrderItems.AsNoTracking().Where(i => i.SalesOrderId == orderId).Select(i => i.Title).FirstOrDefaultAsync(cancellationToken)
            : null;
        return View(new ClaimDetailViewModel { Claim = claim, Messages = messages, Error = error, OrderTitle = title });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendClaimMessage(long id, string? text, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var error = await _postSale.SendClaimMessageAsync(org, id, text ?? string.Empty, cancellationToken);
            if (error is null)
                TempData["Success"] = "Mensaje enviado en el reclamo.";
            else
            {
                TempData["Error"] = error;
                TempData["DraftMessage"] = text;
            }
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Sending claim message failed for claim {Claim}.", id);
            TempData["Error"] = "Mercado Libre no respondió; tu mensaje no se envió. Intenta de nuevo.";
            TempData["DraftMessage"] = text;
        }
        return RedirectToAction(nameof(Claim), new { id });
    }
}
