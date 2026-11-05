using System.Security.Claims;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Services.Tenancy;
using MarketplaceSync.Web.ViewModels.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

[Authorize]
public sealed class InventoryController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly IOrganizationContext _organizationContext;
    private readonly MarketplaceSync.Services.MercadoLibre.IStockSyncScheduler _stockSync;
    private readonly MarketplaceSync.Services.Inventory.InventoryLotService _lots;

    public InventoryController(
        AppDbContext dbContext,
        IOrganizationContext organizationContext,
        MarketplaceSync.Services.MercadoLibre.IStockSyncScheduler stockSync,
        MarketplaceSync.Services.Inventory.InventoryLotService lots)
    {
        _lots = lots;
        _dbContext = dbContext;
        _organizationContext = organizationContext;
        _stockSync = stockSync;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? search,
        bool lowStockOnly,
        CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var organizationVariants = _dbContext.ProductVariants
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive);
        var filteredVariants = organizationVariants;

        if (search is not null)
        {
            var pattern = $"%{search}%";
            filteredVariants = filteredVariants.Where(x =>
                EF.Functions.ILike(x.Sku, pattern) ||
                (x.Barcode != null && EF.Functions.ILike(x.Barcode, pattern)) ||
                (x.Product.Title != null && EF.Functions.ILike(x.Product.Title, pattern)));
        }

        if (lowStockOnly)
            filteredVariants = filteredVariants.Where(x => x.Stock <= x.LowStockThreshold);

        var variants = await filteredVariants
            .Include(x => x.Product)
            .OrderBy(x => x.Stock > x.LowStockThreshold)
            .ThenBy(x => x.Stock)
            .ThenBy(x => x.Sku)
            .Select(x => new InventoryVariantRowViewModel
            {
                Variant = x,
                NetSoldUnits = -(x.InventoryMovements
                    .Where(movement => movement.SalesOrderId != null)
                    .Sum(movement => (int?)movement.QuantityDelta) ?? 0)
            })
            .Take(250)
            .ToListAsync(cancellationToken);
        var summary = await organizationVariants
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalVariants = group.Count(),
                TotalUnits = group.Sum(x => x.Stock),
                LowStock = group.Count(x => x.Stock > 0 && x.Stock <= x.LowStockThreshold),
                OutOfStock = group.Count(x => x.Stock <= 0)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var recentMovements = await _dbContext.InventoryMovements
            .AsNoTracking()
            .Include(x => x.ProductVariant)
                .ThenInclude(x => x.Product)
            .Include(x => x.SalesOrder)
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(30)
            .ToListAsync(cancellationToken);

        var expiring = await _lots.GetExpiringAsync(organizationId, MarketplaceSync.Services.Inventory.InventoryLotService.WarningDays, 100, cancellationToken);
        var nextExpiry = await _lots.GetNextExpiryByVariantAsync(organizationId, variants.Select(v => v.Variant.Id).ToList(), cancellationToken);

        return View(new InventoryIndexViewModel
        {
            ExpiringLots = expiring,
            NextExpiryByVariant = nextExpiry,
            Variants = variants,
            RecentMovements = recentMovements,
            Search = search,
            LowStockOnly = lowStockOnly,
            TotalVariants = summary?.TotalVariants ?? 0,
            TotalUnits = summary?.TotalUnits ?? 0,
            LowStockVariants = summary?.LowStock ?? 0,
            OutOfStockVariants = summary?.OutOfStock ?? 0
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(
        int variantId,
        int newStock,
        string? notes,
        DateOnly? expiresOn,
        string? lotCode,
        CancellationToken cancellationToken)
    {
        if (MarketplaceSync.Services.Inventory.InventoryLotService.ValidateExpiry(expiresOn) is { } expiryError)
        {
            TempData["Error"] = expiryError;
            return RedirectToAction(nameof(Index));
        }

        if (newStock is < 0 or > 1_000_000)
        {
            TempData["Error"] = "El stock debe estar entre 0 y 1,000,000 unidades.";
            return RedirectToAction(nameof(Index));
        }

        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        var variant = await _dbContext.ProductVariants
            .SingleOrDefaultAsync(
                x => x.Id == variantId && x.OrganizationId == organizationId,
                cancellationToken);

        if (variant is null)
            return NotFound();

        var adjustment = newStock - variant.Stock;
        if (adjustment == 0)
        {
            TempData["Error"] = $"El SKU {variant.Sku} ya tiene {newStock} unidades.";
            return RedirectToAction(nameof(Index));
        }

        variant.Stock = newStock;
        variant.UpdatedAt = DateTime.UtcNow;
        await _lots.ApplyAsync(organizationId, variant.Id, adjustment, adjustment > 0 ? expiresOn : null, lotCode, "Ajuste", cancellationToken);
        _dbContext.InventoryMovements.Add(new InventoryMovement
        {
            OrganizationId = organizationId,
            ProductVariantId = variant.Id,
            Type = "ManualAdjustment",
            QuantityDelta = adjustment,
            StockAfter = newStock,
            Notes = string.IsNullOrWhiteSpace(notes)
                ? "Ajuste manual de inventario"
                : notes.Trim()[..Math.Min(notes.Trim().Length, 500)],
            CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            CreatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        _stockSync.RequestPush(new[] { variant.ProductId });
        TempData["Success"] = $"Stock de {variant.Sku} actualizado a {newStock} unidades" +
            (adjustment > 0 && expiresOn.HasValue ? $" (lote que caduca el {expiresOn.Value:dd/MM/yyyy})" : "") +
            ". Se enviará a Mercado Libre en unos segundos.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Da de baja las piezas que quedan de un lote (caducado o dañado): baja el stock y lo manda a Mercado Libre.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WriteOffLot(long lotId, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var lot = await _dbContext.InventoryLots
            .Include(x => x.ProductVariant)
            .SingleOrDefaultAsync(x => x.Id == lotId && x.OrganizationId == organizationId && x.WrittenOffAt == null, cancellationToken);
        if (lot is null)
            return NotFound();

        var variant = lot.ProductVariant;
        var units = Math.Min(lot.QuantityRemaining, Math.Max(0, variant.Stock));
        lot.QuantityRemaining = 0;
        lot.WrittenOffAt = DateTime.UtcNow;

        if (units > 0)
        {
            variant.Stock -= units;
            variant.UpdatedAt = DateTime.UtcNow;
            _dbContext.InventoryMovements.Add(new InventoryMovement
            {
                OrganizationId = organizationId,
                ProductVariantId = variant.Id,
                Type = "ExpiredWriteOff",
                QuantityDelta = -units,
                StockAfter = variant.Stock,
                ReferenceKey = $"lot-writeoff:{lot.Id}",
                Notes = $"Baja de lote{(lot.LotCode is null ? "" : " " + lot.LotCode)} que caduca el {lot.ExpiresOn:dd/MM/yyyy}",
                CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
                CreatedAt = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (units > 0)
            _stockSync.RequestPush(new[] { variant.ProductId });

        TempData["Success"] = units > 0
            ? $"Se dieron de baja {units} piezas de {variant.Sku}. El stock nuevo se enviará a Mercado Libre."
            : $"Lote de {variant.Sku} cerrado (ya no tenía piezas).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetThreshold(
        int variantId,
        int lowStockThreshold,
        CancellationToken cancellationToken)
    {
        if (lowStockThreshold is < 0 or > 1_000_000)
        {
            TempData["Error"] = "El mínimo de stock debe estar entre 0 y 1,000,000.";
            return RedirectToAction(nameof(Index));
        }

        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        var variant = await _dbContext.ProductVariants
            .SingleOrDefaultAsync(
                x => x.Id == variantId && x.OrganizationId == organizationId,
                cancellationToken);

        if (variant is null)
            return NotFound();

        variant.LowStockThreshold = lowStockThreshold;
        variant.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Success"] = $"Alerta de {variant.Sku} configurada en {lowStockThreshold} unidades.";
        return RedirectToAction(nameof(Index));
    }
}
