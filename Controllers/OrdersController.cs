using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services.Tenancy;
using MarketplaceSync.Web.ViewModels.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

[Authorize]
public sealed class OrdersController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly IOrganizationContext _organizationContext;
    private readonly IMercadoLibreOrderSyncService _orderSyncService;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        AppDbContext dbContext,
        IOrganizationContext organizationContext,
        IMercadoLibreOrderSyncService orderSyncService,
        ILogger<OrdersController> logger)
    {
        _dbContext = dbContext;
        _organizationContext = organizationContext;
        _orderSyncService = orderSyncService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? status,
        string? view,
        int? accountId,
        string? search,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        CancellationToken cancellationToken,
        int page = 1)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        const int pageSize = 25;
        page = Math.Max(1, page);
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var organizationOrders = _dbContext.SalesOrders
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId);
        var query = organizationOrders;

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status);

        var nowUtc = DateTime.UtcNow;
        if (view == "toship")
            query = query.ToShip();
        else if (view == "late")
            query = query.LateToShip(nowUtc);
        else
            view = null;

        if (accountId.HasValue)
            query = query.Where(x => x.MarketplaceAccountId == accountId.Value);

        if (search is not null)
        {
            var pattern = $"%{search}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.ExternalOrderId, pattern) ||
                (x.ExternalPackId != null && EF.Functions.ILike(x.ExternalPackId, pattern)) ||
                (x.BuyerNickname != null && EF.Functions.ILike(x.BuyerNickname, pattern)) ||
                (x.BuyerId != null && EF.Functions.ILike(x.BuyerId, pattern)) ||
                x.Items.Any(item =>
                    (item.Title != null && EF.Functions.ILike(item.Title, pattern)) ||
                    (item.SellerSku != null && EF.Functions.ILike(item.SellerSku, pattern))));
        }

        if (dateFrom.HasValue)
        {
            var from = DateTime.SpecifyKind(dateFrom.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(x => x.DateCreated >= from);
        }

        if (dateTo.HasValue)
        {
            var until = DateTime.SpecifyKind(dateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            query = query.Where(x => x.DateCreated < until);
        }

        var filteredOrders = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(filteredOrders / (double)pageSize));
        page = Math.Min(page, totalPages);

        var ordered = view is "toship" or "late"
            ? query.OrderBy(x => x.ShippingDeadline ?? x.DateCreated)
            : query.OrderByDescending(x => x.DateCreated);
        var orders = await ordered
            .AsNoTracking()
            .Include(x => x.Items)
            .Include(x => x.MarketplaceAccount)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var statuses = await organizationOrders
            .Select(x => x.Status)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        var accounts = await _dbContext.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre")
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.ExternalAccountId)
            .ToListAsync(cancellationToken);
        var summary = await organizationOrders
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Paid = group.Count(x => x.Status == "paid"),
                Pending = group.Count(x => x.Status != "paid" && x.Status != "cancelled" && x.Status != "invalid"),
                Cancelled = group.Count(x => x.Status == "cancelled" || x.Status == "invalid"),
                Sales = group.Where(x => x.Status != "cancelled" && x.Status != "invalid")
                    .Sum(x => x.PaidAmount)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var connectedAccounts = await _dbContext.MarketplaceAccounts
            .CountAsync(x => x.OrganizationId == organizationId &&
                             x.Marketplace == "MercadoLibre" &&
                             x.Status == "Connected" &&
                             x.Tokens.Any(token => token.IsActive), cancellationToken);
        var lastSyncAt = await _dbContext.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre")
            .MaxAsync(x => x.LastOrdersSyncAt, cancellationToken);

        var toShip = await organizationOrders.ToShip().CountAsync(cancellationToken);
        var late = await organizationOrders.LateToShip(nowUtc).CountAsync(cancellationToken);

        return View(new OrdersIndexViewModel
        {
            View = view,
            ToShipCount = toShip,
            LateCount = late,
            Orders = orders,
            Statuses = statuses,
            Accounts = accounts,
            SelectedStatus = status,
            SelectedAccountId = accountId,
            Search = search,
            DateFrom = dateFrom,
            DateTo = dateTo,
            TotalOrders = summary?.Total ?? 0,
            FilteredOrders = filteredOrders,
            PaidOrders = summary?.Paid ?? 0,
            PendingOrders = summary?.Pending ?? 0,
            CancelledOrders = summary?.Cancelled ?? 0,
            TotalSales = summary?.Sales ?? 0,
            ConnectedAccounts = connectedAccounts,
            LastSyncAt = lastSyncAt,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        var order = await _dbContext.SalesOrders
            .AsNoTracking()
            .Include(x => x.Items)
            .Include(x => x.MarketplaceAccount)
            .SingleOrDefaultAsync(
                x => x.Id == id && x.OrganizationId == organizationId,
                cancellationToken);

        if (order is null)
            return NotFound();

        var postSale = HttpContext.RequestServices.GetRequiredService<MarketplaceSync.Services.PostSale.PostSaleService>();
        IReadOnlyList<MarketplaceSync.Services.PostSale.ConversationMessage> messages = Array.Empty<MarketplaceSync.Services.PostSale.ConversationMessage>();
        string? messagesError = null;
        try
        {
            (messages, messagesError) = await postSale.GetConversationAsync(organizationId, id, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Conversation unavailable for order {Order}.", id);
            messagesError = "No pude cargar los mensajes ahora.";
        }

        var claims = await _dbContext.OrderClaims.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && (c.SalesOrderId == id || c.ResourceId == order.ExternalOrderId))
            .OrderByDescending(c => c.DateCreated)
            .ToListAsync(cancellationToken);

        return View(new MarketplaceSync.Web.ViewModels.Orders.OrderDetailsViewModel
        {
            Order = order,
            Messages = messages,
            MessagesError = messagesError,
            Claims = claims,
            Blocked = await HttpContext.RequestServices.GetRequiredService<MarketplaceSync.Services.PostSale.BuyerBlacklistService>()
                .FindAsync(organizationId, order.MarketplaceAccountId, order.BuyerId, cancellationToken),
            BuyerOrders = string.IsNullOrWhiteSpace(order.BuyerId) ? 0 : await _dbContext.SalesOrders.CountAsync(
                x => x.OrganizationId == organizationId && x.MarketplaceAccountId == order.MarketplaceAccountId && x.BuyerId == order.BuyerId, cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);

        try
        {
            var result = await _orderSyncService.SyncAllAccountsAsync(
                organizationId,
                cancellationToken);

            if (result.AccountsProcessed == 0 && result.AccountsFailed == 0)
            {
                TempData["Error"] = "No hay una cuenta activa de Mercado Libre para sincronizar.";
            }
            else
            {
                TempData[result.AccountsFailed == 0 ? "Success" : "Error"] =
                    $"Sincronización terminada: {result.OrdersCreated} nuevos, " +
                    $"{result.OrdersUpdated} actualizados, {result.AccountsFailed} cuentas con error.";
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Manual Mercado Libre order sync failed.");
            TempData["Error"] = "No fue posible sincronizar los pedidos. Revisa la conexión de Mercado Libre.";
        }

        return RedirectToAction(nameof(Index));
    }

    // ─────────────────────────────────────────────────────────────
    // Despacho: etiquetas en lote, lista de empaque y empacar con escáner
    // ─────────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Labels(int[] ids, bool thermal, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var labels = HttpContext.RequestServices.GetRequiredService<MarketplaceSync.Services.Shipping.ShippingLabelService>();
        try
        {
            var (file, error) = await labels.GetLabelsAsync(org, ids, thermal, cancellationToken);
            if (file is null)
            {
                TempData["Error"] = error;
                return RedirectToAction(nameof(Index), new { view = "toship" });
            }
            if (file.Skipped.Count > 0)
                TempData["Warning"] = "No se incluyeron: " + string.Join("; ", file.Skipped.Take(6));
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Batch labels failed.");
            TempData["Error"] = "Mercado Libre no entregó las etiquetas. Intenta de nuevo en un momento.";
            return RedirectToAction(nameof(Index), new { view = "toship" });
        }
    }

    /// <summary>Lista de empaque imprimible: piezas agrupadas por producto y pedidos con casillas.</summary>
    [HttpGet]
    public async Task<IActionResult> PickList([FromQuery] int[] ids, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var query = _dbContext.SalesOrders.AsNoTracking().Include(o => o.Items).Where(o => o.OrganizationId == org);
        query = ids.Length > 0 ? query.Where(o => ids.Contains(o.Id)) : query.ToShip();
        var orders = await query.OrderBy(o => o.ShippingDeadline ?? o.DateCreated).Take(200).ToListAsync(cancellationToken);
        ViewBag.Images = await ItemImagesAsync(org, orders.SelectMany(o => o.Items.Select(i => i.ExternalItemId)).Distinct().ToList(), cancellationToken);
        return View(orders);
    }

    /// <summary>Pantalla para empacar con lector de código de barras (o la cámara del celular).</summary>
    [HttpGet]
    public IActionResult Pack() => View();

    /// <summary>Busca el pedido por número de envío (código de la etiqueta), de pedido o de carrito.</summary>
    [HttpGet]
    public async Task<IActionResult> PackLookup(string? code, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        // El QR de la etiqueta de Mercado Envíos trae JSON ({"id":"4412345678","t":"lm"}); el código de barras, solo el número.
        var qr = System.Text.RegularExpressions.Regex.Match(code ?? string.Empty, "\"id\"\\s*:\\s*\"?(\\d+)");
        code = qr.Success ? qr.Groups[1].Value : new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        if (code.Length < 2)
            return Json(new { ok = false, message = "Escanea el código de barras de la etiqueta." });
        var orders = await _dbContext.SalesOrders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.OrganizationId == org && (o.ShippingId == code || o.ExternalOrderId == code || o.ExternalPackId == code))
            .ToListAsync(cancellationToken);
        if (orders.Count == 0)
            return Json(new { ok = false, message = $"No encontré un pedido con el código {code}. Sincroniza los pedidos e intenta de nuevo." });

        var itemIds = orders.SelectMany(o => o.Items.Select(i => i.ExternalItemId)).Distinct().ToList();
        var images = await ItemImagesAsync(org, itemIds, cancellationToken);
        var skus = orders.SelectMany(o => o.Items).Select(i => i.SellerSku).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        var barcodes = await _dbContext.ProductVariants.AsNoTracking()
            .Where(v => v.OrganizationId == org && skus.Contains(v.Sku))
            .Select(v => new { v.Sku, v.Barcode }).ToListAsync(cancellationToken);
        var cancelled = orders.Any(o => o.Status == "cancelled");
        return Json(new
        {
            ok = true,
            orderIds = orders.Select(o => o.Id),
            title = orders.Count == 1 ? $"Pedido #{orders[0].ExternalOrderId}" : $"Carrito con {orders.Count} pedidos",
            buyer = orders[0].BuyerNickname,
            warning = cancelled ? "¡Este pedido está CANCELADO! No lo envíes." : orders.Any(o => o.PackedAt.HasValue) ? "Este paquete ya se había empacado antes." : null,
            items = orders.SelectMany(o => o.Items).Select(i => new
            {
                title = i.Title ?? i.ExternalItemId,
                quantity = i.Quantity,
                sku = i.SellerSku,
                codes = new[] { i.SellerSku, barcodes.FirstOrDefault(b => b.Sku == i.SellerSku)?.Barcode, i.ExternalItemId }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new string(x!.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant()),
                image = images.GetValueOrDefault(i.ExternalItemId)
            })
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PackConfirm([FromForm] int[] ids, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var orders = await _dbContext.SalesOrders.Where(o => o.OrganizationId == org && ids.Contains(o.Id)).ToListAsync(cancellationToken);
        foreach (var o in orders) o.PackedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Json(new { ok = orders.Count > 0 });
    }

    private async Task<Dictionary<string, string?>> ItemImagesAsync(Guid org, List<string> itemIds, CancellationToken cancellationToken) =>
        (await _dbContext.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.ExternalItemId != null && itemIds.Contains(p.ExternalItemId))
            .Select(p => new { Id = p.ExternalItemId!, p.Product.ImageUrl })
            .ToListAsync(cancellationToken))
        .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().ImageUrl);
}
