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
    public IReadOnlyList<BlockedBuyerRow> Blocked { get; init; } = Array.Empty<BlockedBuyerRow>();
    public IReadOnlyList<OrderClaim> PendingReturns { get; init; } = Array.Empty<OrderClaim>();
    public IReadOnlyList<MarketplaceSync.Web.Models.PostSale.MessageTemplate> Templates { get; init; } = Array.Empty<MarketplaceSync.Web.Models.PostSale.MessageTemplate>();
}

public sealed class ClaimDetailViewModel
{
    public required OrderClaim Claim { get; init; }
    public IReadOnlyList<ClaimMessage> Messages { get; init; } = Array.Empty<ClaimMessage>();
    public string? Error { get; init; }
    public string? OrderTitle { get; init; }
    public BlockedBuyer? Blocked { get; init; }
    public string? BuyerNickname { get; init; }
    public IReadOnlyList<ReturnLine> ReturnLines { get; init; } = Array.Empty<ReturnLine>();
}

/// <summary>Postventa: mensajes sin responder y reclamos, sin entrar a Mercado Libre.</summary>
[Authorize]
public sealed class PostSaleController : Controller
{
    private readonly PostSaleService _postSale;
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly ILogger<PostSaleController> _logger;
    private readonly BuyerBlacklistService _blacklist;
    private readonly ReturnRestockService _returns;

    public PostSaleController(PostSaleService postSale, AppDbContext context, IOrganizationContext organizationContext, ILogger<PostSaleController> logger,
        BuyerBlacklistService blacklist, ReturnRestockService returns)
    {
        _returns = returns;
        _blacklist = blacklist;
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
            Tab = tab is "blocked" or "templates" ? tab : tab == "claims" || (tab is null && pending.Count == 0 && open.Count > 0) ? "claims" : "messages",
            Templates = await HttpContext.RequestServices.GetRequiredService<MessageTemplateService>().ListAsync(org, cancellationToken),
            Blocked = await _blacklist.ListAsync(org, cancellationToken),
            PendingReturns = await _context.OrderClaims.AsNoTracking()
                .Where(c => c.OrganizationId == org && c.HasReturn && c.ReturnRestockedAt == null)
                .OrderByDescending(c => c.LastUpdated).Take(30).ToListAsync(cancellationToken),
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

    // ── Mensajes rápidos ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTemplate(int? id, string? title, string? text, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await HttpContext.RequestServices.GetRequiredService<MessageTemplateService>().SaveAsync(org, id, title, text, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Index), null, new { tab = "templates" }, id is { } t ? $"tpl-{t}" : "nuevo");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplate(int id, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var ok = await HttpContext.RequestServices.GetRequiredService<MessageTemplateService>().DeleteAsync(org, id, cancellationToken);
        TempData[ok ? "Success" : "Error"] = ok ? "Mensaje rápido borrado." : "No encontré ese mensaje.";
        return RedirectToAction(nameof(Index), new { tab = "templates" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(int orderId, string? text, int? templateId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var error = await _postSale.SendMessageAsync(org, orderId, text ?? string.Empty, cancellationToken);
            if (error is null && templateId is > 0)
                await HttpContext.RequestServices.GetRequiredService<MessageTemplateService>().MarkUsedAsync(org, templateId.Value, cancellationToken);
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
        var order = claim.SalesOrderId is { } oid2
            ? await _context.SalesOrders.AsNoTracking().Where(o => o.Id == oid2).Select(o => new { o.BuyerId, o.BuyerNickname, o.MarketplaceAccountId }).FirstOrDefaultAsync(cancellationToken)
            : null;
        return View(new ClaimDetailViewModel
        {
            Claim = claim, Messages = messages, Error = error, OrderTitle = title, BuyerNickname = order?.BuyerNickname,
            Blocked = order is null ? null : await _blacklist.FindAsync(org, order.MarketplaceAccountId, order.BuyerId, cancellationToken),
            ReturnLines = await _returns.GetLinesAsync(org, claim, cancellationToken)
        });
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

    // ── Lista negra ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Block(int orderId, string? reason, bool orders, bool questions, long? claimId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var error = await _blacklist.BlockOrderBuyerAsync(org, orderId, reason, orders, questions, cancellationToken);
            if (error is null)
                TempData["Success"] = orders && questions ? "Comprador bloqueado: ya no puede comprarte ni preguntarte."
                    : orders ? "Comprador bloqueado: ya no puede comprarte." : "Comprador bloqueado: ya no puede preguntarte.";
            else
                TempData["Error"] = error;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Blocking buyer failed for order {Order}.", orderId);
            TempData["Error"] = "Mercado Libre no respondió; el comprador no se bloqueó. Intenta de nuevo.";
        }
        return claimId is { } c ? RedirectToAction(nameof(Claim), new { id = c }) : RedirectToAction("Details", "Orders", new { id = orderId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unblock(long id, int? orderId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var error = await _blacklist.UnblockAsync(org, id, cancellationToken);
            if (error is null) TempData["Success"] = "Desbloqueado: ya puede comprarte y preguntarte otra vez.";
            else TempData["Error"] = error;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Unblocking failed {Id}.", id);
            TempData["Error"] = "Mercado Libre no respondió; sigue bloqueado. Intenta de nuevo.";
        }
        return orderId is { } o ? RedirectToAction("Details", "Orders", new { id = o }) : RedirectToAction(nameof(Index), new { tab = "blocked" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportBlocked(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var added = await _blacklist.ImportFromMercadoLibreAsync(org, cancellationToken);
        TempData["Success"] = added == 0 ? "No encontré bloqueados nuevos en Mercado Libre."
            : $"Traje {added} {(added == 1 ? "bloqueado" : "bloqueados")} de Mercado Libre.";
        return RedirectToAction(nameof(Index), new { tab = "blocked" });
    }

    // ── Devoluciones ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restock(long id, string? condition, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var (message, error) = await _returns.RestockAsync(org, id, condition ?? string.Empty, userId, cancellationToken);
            if (error is null) TempData["Success"] = message;
            else TempData["Error"] = error;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Claim), new { id });
    }
}
