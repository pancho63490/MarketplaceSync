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
            Claims = claims
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
}
