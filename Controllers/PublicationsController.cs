using System.Globalization;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.CrossBorder;
using MarketplaceSync.Web.Models.Marketplace;
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
        var generalReserve = await _context.AutomationSettings.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId).Select(a => a.StockReserve).FirstOrDefaultAsync(cancellationToken);

        var rows = publications.Select(p =>
        {
            var product = p.Product;
            var variants = product.Variants.Where(v => v.IsActive).ToList();
            var costKnown = MarketplaceSync.Services.Automation.ProductCost.Known(product);
            decimal? landed = MarketplaceSync.Services.Automation.ProductCost.Landed(product, settings, _pricing);
            CrossBorderProfitEstimate? profit = landed.HasValue && p.Price is > 0
                ? _pricing.CalculateProfitFromLandedCost(
                    landed.Value, p.Price.Value, settings,
                    p.SaleFeeForPrice == p.Price ? p.SaleFeeMxn : null)
                : null;

            decimal? suggested = null;
            if (landed.HasValue && profit is { IsProfitable: false })
            {
                var price = SuggestedPrice(landed.Value, p, settings);
                if (price > (p.Price ?? 0))
                    suggested = price;
            }

            return new PublicationRow
            {
                PublicationId = p.Id,
                SuggestedPriceMxn = suggested,
                ProductId = product.Id,
                ItemId = p.ExternalItemId!,
                Title = product.Title ?? p.ExternalItemId!,
                ImageUrl = product.ImageUrl,
                Permalink = p.Permalink,
                Status = p.Status ?? "active",
                Price = p.Price,
                MercadoLibreStock = p.Stock,
                AppStock = variants.Count > 0 ? variants.Sum(v => Math.Max(0, v.Stock)) : null,
                AppPublishable = variants.Count > 0 ? MarketplaceSync.Services.Inventory.StockReserve.Publishable(variants, generalReserve) : null,
                SkuCount = variants.Count,
                HasRemoteVariations = p.Variants.Count > 0,
                Sold = p.SoldQuantity,
                CostKnown = costKnown,
                CostValue = product.SourcePrice,
                CostCurrency = product.SourceCurrency,
                CostIsReal = product.RealLandedCostMxn is > 0,
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
        var product = await _context.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == item.ProductId && p.OrganizationId == organizationId, cancellationToken);

        // Promociones: en cuál está y a cuáles puede entrar (lectura en vivo de Mercado Libre).
        var promotions = HttpContext.RequestServices.GetRequiredService<PromotionService>();
        var offers = item.IsClosed ? new List<PromotionOffer>() : await promotions.RefreshAsync(organizationId, id, cancellationToken);
        var publication = await _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == id && p.Product.OrganizationId == organizationId, cancellationToken);
        var campaignRows = new List<CampaignRow>();
        var discountOptions = new List<DiscountOption>();
        decimal? promotionProfit = null;
        if (publication is not null)
        {
            var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
            discountOptions = promotions.DiscountOptions(publication, settings);
            campaignRows = (offers ?? new())
                .Where(o => o.IsCandidate && o.Type != "PRICE_DISCOUNT" && !string.IsNullOrEmpty(o.PromotionId))
                .Select(o => promotions.BuildRow(publication, o, settings))
                .ToList();
            if (publication.PromotionType is not null && publication.PromotionPrice is > 0)
            {
                var original = PromotionService.BasePrice(publication) ?? publication.PromotionPrice.Value;
                var received = PromotionService.SellerPrice(publication.PromotionPrice, original, publication.PromotionMeliPercent,
                    publication.PromotionMeliPercent is > 0 && original > 0 ? Math.Max(0, (1 - publication.PromotionPrice.Value / original) * 100m - publication.PromotionMeliPercent.Value) : null);
                promotionProfit = HttpContext.RequestServices.GetRequiredService<PublicationEconomics>()
                    .Calculate(publication, publication.Product, settings, received)?.Profit.NetProfitMxn;
            }
        }

        return View(new PublicationEditViewModel
        {
            Item = item,
            LandedCostMxn = landed,
            Profit = profit,
            Compatibilities = product is null ? new() : CompatibilityService.Read(product),
            CompatibilityCount = product?.CompatibilityCount,
            CompatibilitySentAt = product?.CompatibilitySentAt,
            Publication = publication,
            CampaignOffers = campaignRows,
            DiscountOptions = discountOptions,
            PromotionProfit = promotionProfit,
            PromotionsUnavailable = offers is null && !item.IsClosed
        });
    }

    // ── Compatibilidades con vehículos ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompatSuggest(int id, [FromServices] CompatibilityService compat, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var publication = await LoadForCompatAsync(organizationId, id, cancellationToken);
        if (publication is null) return NotFound();

        var (rows, note) = await compat.SuggestAsync(publication.Product, cancellationToken);
        if (rows.Count == 0)
        {
            TempData["Warning"] = "La IA no pudo saber con seguridad con qué vehículos es compatible" +
                                  (string.IsNullOrWhiteSpace(note) ? "." : $": {note}") + " Captúralos a mano.";
            return Redirect(Url.Action(nameof(Edit), new { id }) + "#pe-compat");
        }

        await compat.ValidateAsync(organizationId, publication.MarketplaceAccountId!.Value, rows, cancellationToken);
        CompatibilityService.Write(publication.Product, rows);
        await _context.SaveChangesAsync(cancellationToken);
        var bad = rows.Count(r => r.Vehicles == 0);
        TempData["Success"] = $"La IA sugirió {rows.Count} {(rows.Count == 1 ? "vehículo" : "vehículos")}" +
                              (bad > 0 ? $"; {bad} no existe así en Mercado Libre (corrígelo)" : "") +
                              ". Revísalos antes de mandarlos.";
        return Redirect(Url.Action(nameof(Edit), new { id }) + "#pe-compat");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompatSave(int id, List<VehicleFitment> rows, bool send, bool replace, [FromServices] CompatibilityService compat, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var publication = await LoadForCompatAsync(organizationId, id, cancellationToken);
        if (publication is null) return NotFound();

        var previous = CompatibilityService.Read(publication.Product);
        var clean = CompatibilityService.Normalize(rows ?? new());
        foreach (var row in clean)
        {
            // Si el renglón es igual a uno de la IA, conserva el origen.
            var same = previous.FirstOrDefault(p => p.Label.Equals(row.Label, StringComparison.OrdinalIgnoreCase));
            row.Source = same?.Source ?? "manual";
        }
        await compat.ValidateAsync(organizationId, publication.MarketplaceAccountId!.Value, clean, cancellationToken);
        CompatibilityService.Write(publication.Product, clean);
        await _context.SaveChangesAsync(cancellationToken);

        var bad = clean.Where(r => r.Vehicles == 0).Select(r => r.Label).ToList();
        if (!send)
        {
            TempData[bad.Count > 0 ? "Warning" : "Success"] = bad.Count > 0
                ? $"Guardado. Mercado Libre no reconoce: {string.Join(", ", bad.Take(3))}. Revisa cómo se escribe la marca o el modelo."
                : $"Guardado: {clean.Count} {(clean.Count == 1 ? "renglón" : "renglones")} validados.";
            return Redirect(Url.Action(nameof(Edit), new { id }) + "#pe-compat");
        }

        var (ok, _, message) = await compat.SendToItemAsync(organizationId, id, replace, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message + (bad.Count > 0 ? $" No se mandaron: {string.Join(", ", bad.Take(3))}." : "");
        return Redirect(Url.Action(nameof(Edit), new { id }) + "#pe-compat");
    }

    private Task<MarketplacePublication?> LoadForCompatAsync(Guid organizationId, int id, CancellationToken cancellationToken) =>
        _context.MarketplacePublications
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == id && p.Product.OrganizationId == organizationId &&
                                      p.ExternalItemId != null && p.MarketplaceAccountId != null, cancellationToken);

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
    public async Task<IActionResult> Bulk(int[] ids, string? bulkAction, string? amount, string? mode, string? filter,
        int? percent, int? days, bool includeLosing, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (ids.Length == 0)
        {
            TempData["Error"] = "Selecciona al menos una publicación.";
            return RedirectToAction(nameof(Index), new { filter });
        }

        if (bulkAction == "suggested")
            return await ApplySuggestedPricesAsync(organizationId, ids, filter, cancellationToken);
        if (bulkAction is "discount" or "undiscount")
            return await BulkDiscountAsync(organizationId, ids, bulkAction, percent ?? 10, days ?? 7, includeLosing, filter, cancellationToken);

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

    /// <summary>
    /// Descuento individual a varias publicaciones (o quitarlo). Las que perderían con el descuento se saltan,
    /// salvo que se marque "también las que pierden".
    /// </summary>
    private async Task<IActionResult> BulkDiscountAsync(Guid organizationId, int[] ids, string action, int percent, int days, bool includeLosing, string? filter, CancellationToken cancellationToken)
    {
        var promotions = HttpContext.RequestServices.GetRequiredService<PromotionService>();
        var economics = HttpContext.RequestServices.GetRequiredService<PublicationEconomics>();
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product)
            .Where(p => ids.Contains(p.Id) && p.Product.OrganizationId == organizationId && p.ExternalItemId != null)
            .Take(50)
            .ToListAsync(cancellationToken);

        var done = 0;
        var skipped = new List<string>();
        var errors = new List<string>();
        foreach (var publication in publications)
        {
            var title = publication.Product.Title.Length > 35 ? publication.Product.Title[..35] + "…" : publication.Product.Title;
            if (action == "undiscount")
            {
                if (publication.PromotionType is null) { skipped.Add($"«{title}» no tenía"); continue; }
                var (left, leaveMessage) = await promotions.LeaveAsync(organizationId, publication.Id, cancellationToken);
                if (left) done++; else errors.Add($"«{title}»: {leaveMessage}");
                continue;
            }

            if (publication.PromotionType is not null) { skipped.Add($"«{title}» ya tiene promoción"); continue; }
            var basePrice = PromotionService.BasePrice(publication);
            if (basePrice is not > 0) { skipped.Add($"«{title}» sin precio"); continue; }
            var money = economics.Calculate(publication, publication.Product, settings, PromotionService.DiscountedPrice(basePrice.Value, percent));
            if (money is not null && money.Profit.NetProfitMxn < 0 && !includeLosing)
            {
                skipped.Add($"«{title}» perdería {(-money.Profit.NetProfitMxn).ToString("C0", Mx)}");
                continue;
            }
            var (ok, message) = await promotions.CreateDiscountAsync(organizationId, publication.Id, percent, days, cancellationToken);
            if (ok) done++; else errors.Add($"«{title}»: {message}");
        }

        if (done > 0)
            TempData["Success"] = action == "undiscount"
                ? $"Quité la promoción de {done} {(done == 1 ? "publicación" : "publicaciones")}."
                : $"{percent}% de descuento por {days} días en {done} {(done == 1 ? "publicación" : "publicaciones")}.";
        if (skipped.Count > 0)
            TempData["Warning"] = "Sin cambio: " + string.Join(" · ", skipped.Take(5)) + (skipped.Count > 5 ? $" (y {skipped.Count - 5} más)" : "") + ".";
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" · ", errors.Take(4)) + (errors.Count > 4 ? $" (y {errors.Count - 4} más)" : "");
        if (done == 0 && skipped.Count == 0 && errors.Count == 0)
            TempData["Warning"] = "No encontré esas publicaciones.";
        return RedirectToAction(nameof(Index), new { filter });
    }

    /// <summary>
    /// Precio sugerido con la comisión real de la publicación cuando se conoce (Premium o Clásica).
    /// </summary>
    private decimal SuggestedPrice(decimal landed, MarketplacePublication publication, CrossBorderSettings settings)
    {
        decimal? feePercent = publication.SaleFeeMxn is > 0 && publication.SaleFeeForPrice is >= 299m
            ? Math.Round(publication.SaleFeeMxn.Value / publication.SaleFeeForPrice.Value * 100m, 2)
            : null;
        return _pricing.SuggestPriceFromLandedCost(landed, settings, feePercent);
    }

    /// <summary>
    /// Sube al precio sugerido las publicaciones que no llegan a la ganancia mínima.
    /// Nunca baja un precio y deja igual las que ya ganan lo mínimo.
    /// </summary>
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private async Task<IActionResult> ApplySuggestedPricesAsync(Guid organizationId, int[] ids, string? filter, CancellationToken cancellationToken)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var publications = await _context.MarketplacePublications
            .AsNoTracking()
            .Include(p => p.Product)
            .Where(p => ids.Contains(p.Id) && p.Product.OrganizationId == organizationId && p.ExternalItemId != null)
            .Take(100)
            .ToListAsync(cancellationToken);

        var changed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        foreach (var publication in publications)
        {
            var product = publication.Product;
            var label = product.Title is { Length: > 40 } t ? t[..40] + "…" : product.Title ?? publication.ExternalItemId!;
            var costKnown = MarketplaceSync.Services.Automation.ProductCost.Known(product);
            if (!costKnown || publication.Price is not > 0)
            {
                skipped.Add($"{label}: sin costo");
                continue;
            }

            var landed = MarketplaceSync.Services.Automation.ProductCost.Landed(product, settings, _pricing)!.Value;
            var current = _pricing.CalculateProfitFromLandedCost(landed, publication.Price.Value, settings,
                publication.SaleFeeForPrice == publication.Price ? publication.SaleFeeMxn : null);
            var target = SuggestedPrice(landed, publication, settings);
            if (current.IsProfitable || target <= publication.Price.Value)
            {
                skipped.Add($"{label}: ya gana lo mínimo");
                continue;
            }

            try
            {
                var result = await _editor.UpdateAsync(organizationId, publication.Id,
                    new ItemEdit { Price = target.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
                if (result.Ok)
                    changed.Add($"{label}: {publication.Price.Value.ToString("C0", Mx)} → {target.ToString("C0", Mx)}");
                else
                    errors.Add($"{label}: {string.Join(" ", result.Errors)}");
            }
            catch (MercadoLibreEditException exception)
            {
                errors.Add($"{label}: {exception.Message}");
            }
        }

        if (changed.Count > 0)
            TempData["Success"] = string.Format(Mx, "{0} con precio sugerido: {1}.",
                changed.Count == 1 ? "1 publicación" : $"{changed.Count} publicaciones",
                string.Join(" · ", changed.Take(5)) + (changed.Count > 5 ? $" (y {changed.Count - 5} más)" : ""));
        else if (errors.Count == 0)
            TempData["Warning"] = "No cambié nada: " + string.Join(" · ", skipped.Take(5));
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" · ", errors.Take(5)) + (errors.Count > 5 ? $" (y {errors.Count - 5} más)" : "");
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
        if (product is null || !MarketplaceSync.Services.Automation.ProductCost.Known(product))
            return (null, null);
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var landed = MarketplaceSync.Services.Automation.ProductCost.Landed(product, settings, _pricing)!.Value;
        var publication = await _context.MarketplacePublications.AsNoTracking().FirstOrDefaultAsync(p => p.Id == publicationId, cancellationToken);
        var realFee = publication?.SaleFeeForPrice == price ? publication.SaleFeeMxn : null;
        return (landed, _pricing.CalculateProfitFromLandedCost(landed, price, settings, realFee));
    }
}
