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
        CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        var query = _dbContext.SalesOrders
            .AsNoTracking()
            .Include(x => x.Items)
            .Include(x => x.MarketplaceAccount)
            .Where(x => x.OrganizationId == organizationId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status);

        var orders = await query
            .OrderByDescending(x => x.DateCreated)
            .Take(200)
            .ToListAsync(cancellationToken);
        var statuses = await _dbContext.SalesOrders
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.Status)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
        var totalOrders = await _dbContext.SalesOrders
            .CountAsync(x => x.OrganizationId == organizationId, cancellationToken);
        var connectedAccounts = await _dbContext.MarketplaceAccounts
            .CountAsync(x => x.OrganizationId == organizationId &&
                             x.Marketplace == "MercadoLibre" &&
                             x.Status == "Connected" &&
                             x.Tokens.Any(token => token.IsActive), cancellationToken);
        var lastSyncAt = await _dbContext.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre")
            .MaxAsync(x => x.LastOrdersSyncAt, cancellationToken);

        return View(new OrdersIndexViewModel
        {
            Orders = orders,
            Statuses = statuses,
            SelectedStatus = status,
            TotalOrders = totalOrders,
            ConnectedAccounts = connectedAccounts,
            LastSyncAt = lastSyncAt
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
