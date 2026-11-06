using System.Globalization;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Services.Tenancy;
using MarketplaceSync.Web.ViewModels.Publications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Publicaciones activas en Mercado Libre con su ganancia por pieza y su stock.</summary>
[Authorize]
public sealed class PublicationsController : Controller
{
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly ICrossBorderPricingService _pricing;
    private readonly IMercadoLibreListingSyncService _sync;
    private readonly ILogger<PublicationsController> _logger;
    private readonly MercadoLibreItemEditorService _editor;

    public PublicationsController(
        AppDbContext context,
        IOrganizationContext organizationContext,
        ICrossBorderPricingService pricing,
        IMercadoLibreListingSyncService sync,
        ILogger<PublicationsController> logger,
        MercadoLibreItemEditorService editor)
    {
        _editor = editor;
        _context = context;
        _organizationContext = organizationContext;
        _pricing = pricing;
        _sync = sync;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? filter, string? q, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);

        var publications = await _context.MarketplacePublications
            .AsNoTracking()
            .Include(p => p.Product).ThenInclude(p => p.Variants)
            .Include(p => p.MarketplaceAccount)
            .Where(p => p.Product.OrganizationId == organizationId &&
                        p.Marketplace == "MercadoLibre" &&
                        p.IsPublished &&
                        p.ExternalItemId != null)
            .ToListAsync(cancellationToken);

        var rows = publications.Select(p =>
        {
            var product = p.Product;
            var variants = product.Variants.Where(v => v.IsActive).ToList();
            var costKnown = product.SourcePrice is > 0 &&
                            product.SourceMarketplace is not ("MercadoLibre" or "MercadoLibreInsights");
            decimal? landed = costKnown
                ? _pricing.LandedCostMxn(product.SourcePrice!.Value, product.SourceCurrency, settings)
                : null;
            CrossBorderProfitEstimate? profit = landed.HasValue && p.Price is > 0
                ? _pricing.CalculateProfitFromLandedCost(
                    landed.Value, p.Price.Value, settings,
                    p.SaleFeeForPrice == p.Price ? p.SaleFeeMxn : null)
                : null;

            return new PublicationRow
            {
                PublicationId = p.Id,
                ProductId = product.Id,
                ItemId = p.ExternalItemId!,
                Title = product.Title ?? p.ExternalItemId!,
                ImageUrl = product.ImageUrl,
                Permalink = p.Permalink,
                Status = p.Status ?? "active",
                Price = p.Price,
                MercadoLibreStock = p.Stock,
                AppStock = variants.Count > 0 ? variants.Sum(v => Math.Max(0, v.Stock)) : null,
                SkuCount = variants.Count,
                HasRemoteVariations = p.Variants.Count > 0,
                Sold = p.SoldQuantity,
                CostKnown = costKnown,
                CostValue = product.SourcePrice,
                CostCurrency = product.SourceCurrency,
                LandedCostMxn = landed,
                Profit = profit,
                Kit = landed.HasValue && p.Price is > 0 ? _pricing.SuggestKit(landed.Value, p.Price.Value, settings) : null,
                FeeIsReal = p.SaleFeeForPrice == p.Price && p.SaleFeeMxn.HasValue,
                LastSyncedAt = p.LastSyncedAt,
                LastStockPushError = p.LastStockPushError,
                AccountName = p.MarketplaceAccount?.DisplayName
            };
        }).ToList();

        var search = q?.Trim();
        IEnumerable<PublicationRow> filtered = rows;
        if (!string.IsNullOrWhiteSpace(search))
            filtered = filtered.Where(r => r.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                           r.ItemId.Contains(search, StringComparison.OrdinalIgnoreCase));
        filtered = filter switch
        {
            "nocost" => filtered.Where(r => !r.CostKnown),
            "loss" => filtered.Where(r => r.Profit is { NetProfitMxn: < 0 }),
            "paused" => filtered.Where(r => r.Status != "active"),
            "mismatch" => filtered.Where(r => r.StockMismatch),
            _ => filtered
        };

        var active = rows.Where(r => r.Status == "active").ToList();
        return View(new PublicationsIndexViewModel
        {
            Rows = filtered
                .OrderBy(r => r.Status == "active" ? 0 : 1)
                .ThenByDescending(r => r.Sold ?? 0)
                .ToList(),
            Filter = filter,
            Search = search,
            ActiveCount = active.Count,
            PausedCount = rows.Count - active.Count,
            NoCostCount = rows.Count(r => !r.CostKnown),
            LossCount = rows.Count(r => r.Profit is { NetProfitMxn: < 0 }),
            MismatchCount = rows.Count(r => r.StockMismatch),
            PotentialProfitMxn = active
                .Where(r => r.Profit != null && r.MercadoLibreStock > 0)
                .Sum(r => r.Profit!.NetProfitMxn * r.MercadoLibreStock!.Value),
            UnitsListed = active.Sum(r => Math.Max(0, r.MercadoLibreStock ?? 0)),
            LastSyncedAt = rows.Count > 0 ? rows.Max(r => r.LastSyncedAt) : null,
            HasConnectedAccount = await _context.MarketplaceAccounts.AnyAsync(
                x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre" && x.Status == "Connected",
                cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var result = await _sync.SyncOrganizationAsync(organizationId, cancellationToken);
            if (result.Accounts > 0 || result.Listings > 0)
            {
                TempData["Success"] = $"Sincronizado: {result.Listings} publicaciones" +
                    (result.Imported > 0 ? $", {result.Imported} nuevas en tu catálogo" : "") +
                    (result.StockPushed > 0 ? $", stock actualizado en {result.StockPushed}" : "") + ".";
            }
            if (result.Warnings.Count > 0)
                TempData["Warning"] = string.Join(" ", result.Warnings);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Manual listing sync failed for {OrganizationId}.", organizationId);
            TempData["Error"] = "No fue posible sincronizar con Mercado Libre. Intenta de nuevo en un momento.";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Guarda lo que te costó la pieza para calcular la ganancia.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCost(int productId, string? cost, string? currency, string? filter, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.OrganizationId == organizationId, cancellationToken);
        if (product is null)
            return NotFound();

        var clean = (cost ?? string.Empty).Replace("$", string.Empty).Replace(",", ".").Trim();
        if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value is <= 0 or > 1_000_000)
        {
            TempData["Error"] = "Escribe el costo como número, por ejemplo 38.50.";
            return RedirectToAction(nameof(Index), new { filter });
        }

        product.SourcePrice = Math.Round(value, 2);
        product.SourceCurrency = currency == "MXN" ? "MXN" : "USD";
        if (product.SourceMarketplace is "MercadoLibre" or "MercadoLibreInsights")
            product.SourceMarketplace = "Manual";
        product.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Costo guardado. La ganancia ya se calcula con él.";
        return RedirectToAction(nameof(Index), new { filter });
    }

    /// <summary>
    /// Empieza a controlar el stock desde la app: crea un SKU con las piezas que hoy tiene Mercado Libre.
    /// A partir de ahí, ventas, recepciones y ajustes de la app se envían a Mercado Libre.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManageStock(int publicationId, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var publication = await _context.MarketplacePublications
            .Include(p => p.Product).ThenInclude(p => p.Variants)
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == organizationId, cancellationToken);
        if (publication is null)
            return NotFound();

        if (publication.Product.Variants.Any(v => v.IsActive))
        {
            TempData["Error"] = "Este producto ya controla su stock desde la app.";
            return RedirectToAction(nameof(Index));
        }

        var baseSku = $"ML-{publication.ExternalItemId}";
        var sku = baseSku;
        var suffix = 1;
        while (await _context.ProductVariants.AnyAsync(v => v.OrganizationId == organizationId && v.Sku == sku, cancellationToken))
            sku = $"{baseSku}-{++suffix}";

        publication.Product.Variants.Add(new ProductVariant
        {
            OrganizationId = organizationId,
            Sku = sku,
            AttributesJson = "{}",
            Price = publication.Price,
            Stock = Math.Max(0, publication.Stock ?? 0),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);

        TempData["Success"] = $"Listo: el SKU {sku} empieza con {publication.Stock ?? 0} piezas. " +
                              "Desde ahora las ventas, recepciones y ajustes de Inventario actualizan Mercado Libre.";
        return RedirectToAction(nameof(Index));
    }

    // ─────────────────────────────────────────────────────────────
    // Editar en Mercado Libre sin entrar a Mercado Libre
    // ─────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        ItemDetails item;
        try
        {
            item = await _editor.GetAsync(organizationId, id, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (MercadoLibreEditException exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToAction(nameof(Index));
        }

        var (landed, profit) = await ProfitAsync(organizationId, item.ProductId, item.Price, id, cancellationToken);
        return View(new PublicationEditViewModel { Item = item, LandedCostMxn = landed, Profit = profit });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ItemEdit edit, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var result = await _editor.UpdateAsync(organizationId, id, edit, cancellationToken);
            if (result.Changed.Count > 0)
                TempData["Success"] = "Guardado en Mercado Libre: " + string.Join(", ", result.Changed) + ".";
            else if (result.Ok)
                TempData["Success"] = "No había cambios por guardar.";
            if (!result.Ok)
                TempData["Error"] = string.Join(" ", result.Errors);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (MercadoLibreEditException exception)
        {
            TempData["Error"] = exception.Message;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Editing publication {Id} failed.", id);
            TempData["Error"] = "Mercado Libre no respondió. Tus cambios no se guardaron; intenta de nuevo en un momento.";
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, string status, string? returnTo, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var error = await _editor.SetStatusAsync(organizationId, id, status, cancellationToken);
            if (error is null)
                TempData["Success"] = status switch
                {
                    "paused" => "Publicación pausada: ya no aparece en Mercado Libre hasta que la actives.",
                    "active" => "Publicación activa otra vez.",
                    _ => "Publicación finalizada. Si la quieres de vuelta, usa Republicar."
                };
            else
                TempData["Error"] = "Mercado Libre no lo permitió: " + error;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (MercadoLibreEditException exception)
        {
            TempData["Error"] = exception.Message;
        }
        return returnTo == "index" ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Relist(int id, string? price, int quantity, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (!MercadoLibreItemEditorService.TryParseMoney(price, out var value) || value < 1 || quantity < 1)
        {
            TempData["Error"] = "Para republicar escribe el precio y al menos 1 pieza.";
            return RedirectToAction(nameof(Edit), new { id });
        }
        try
        {
            var (newItemId, error) = await _editor.RelistAsync(organizationId, id, value, quantity, cancellationToken);
            if (error is null)
                TempData["Success"] = $"Republicada como {newItemId}. Las ventas y preguntas viejas se quedan en la publicación anterior.";
            else
                TempData["Error"] = "Mercado Libre no la republicó: " + error;
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (MercadoLibreEditException exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(int[] ids, string? bulkAction, string? amount, string? mode, string? filter, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (ids.Length == 0)
        {
            TempData["Error"] = "Selecciona al menos una publicación.";
            return RedirectToAction(nameof(Index), new { filter });
        }

        decimal? value = null;
        if (bulkAction == "price")
        {
            if (!MercadoLibreItemEditorService.TryParseMoney(amount?.Replace("+", string.Empty), out var parsed) || parsed == 0)
            {
                TempData["Error"] = "Escribe cuánto cambiar el precio, por ejemplo 5 (sube 5%) o -100 (baja $100).";
                return RedirectToAction(nameof(Index), new { filter });
            }
            value = parsed;
        }

        var result = await _editor.BulkAsync(organizationId, ids, bulkAction ?? string.Empty, value, mode != "amount", cancellationToken);
        if (result.Done > 0)
        {
            var what = bulkAction switch { "paused" => "pausadas", "active" => "activadas", _ => "con precio nuevo" };
            TempData["Success"] = $"{result.Done} {(result.Done == 1 ? "publicación" : "publicaciones")} {what} en Mercado Libre.";
        }
        if (result.Errors.Count > 0)
            TempData["Error"] = string.Join(" · ", result.Errors.Take(5)) + (result.Errors.Count > 5 ? $" (y {result.Errors.Count - 5} más)" : "");
        return RedirectToAction(nameof(Index), new { filter });
    }

    /// <summary>Ganancia por pieza si vendes a ese precio (para el editor, mientras escribes).</summary>
    [HttpGet]
    public async Task<IActionResult> ProfitPreview(int id, string? price, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (!MercadoLibreItemEditorService.TryParseMoney(price, out var value) || value <= 0)
            return Json(new { ok = false });
        var productId = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Id == id && p.Product.OrganizationId == organizationId)
            .Select(p => (int?)p.ProductId).FirstOrDefaultAsync(cancellationToken);
        if (productId is null)
            return NotFound();
        var (landed, profit) = await ProfitAsync(organizationId, productId.Value, value, id, cancellationToken);
        if (profit is null)
            return Json(new { ok = false, noCost = landed is null });
        var mx = CultureInfo.GetCultureInfo("es-MX");
        return Json(new
        {
            ok = true,
            profit = profit.NetProfitMxn.ToString("C0", mx),
            margin = profit.NetMarginPercent.ToString("0.#", mx) + "%",
            fee = profit.MarketplaceFeeMxn.ToString("C0", mx),
            loss = profit.NetProfitMxn < 0
        });
    }

    private async Task<(decimal? Landed, CrossBorderProfitEstimate? Profit)> ProfitAsync(
        Guid organizationId, int productId, decimal price, int publicationId, CancellationToken cancellationToken)
    {
        var product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null || product.SourcePrice is not > 0 || product.SourceMarketplace is "MercadoLibre" or "MercadoLibreInsights")
            return (null, null);
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var landed = _pricing.LandedCostMxn(product.SourcePrice.Value, product.SourceCurrency, settings);
        var publication = await _context.MarketplacePublications.AsNoTracking().FirstOrDefaultAsync(p => p.Id == publicationId, cancellationToken);
        var realFee = publication?.SaleFeeForPrice == price ? publication.SaleFeeMxn : null;
        return (landed, _pricing.CalculateProfitFromLandedCost(landed, price, settings, realFee));
    }
}
