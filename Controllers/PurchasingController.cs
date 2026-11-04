using System.Globalization;
using System.Security.Claims;
using System.Text;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Services.Tenancy;
using MarketplaceSync.Web.ViewModels.Purchasing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>
/// Radar de compra: qué traer de EE.UU. según la demanda en Mercado Libre y el margen real.
/// </summary>
[Authorize]
public sealed class PurchasingController : Controller
{
    private static readonly HashSet<string> Tabs = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "review", "restock", "transit", "discarded"
    };

    private readonly AppDbContext _dbContext;
    private readonly IOrganizationContext _organizationContext;
    private readonly IPurchaseRadarService _radarService;
    private readonly ICrossBorderPricingService _pricingService;
    private readonly ILogger<PurchasingController> _logger;

    public PurchasingController(
        AppDbContext dbContext,
        IOrganizationContext organizationContext,
        IPurchaseRadarService radarService,
        ICrossBorderPricingService pricingService,
        ILogger<PurchasingController> logger)
    {
        _dbContext = dbContext;
        _organizationContext = organizationContext;
        _radarService = radarService;
        _pricingService = pricingService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? tab, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _pricingService.GetOrCreateSettingsAsync(organizationId, cancellationToken);

        var candidates = await _dbContext.PurchaseCandidates
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);

        var toBuy = candidates
            .Where(x => x.Status == PurchaseCandidateStatus.Suggested && x.DemandScore > 0)
            .OrderByDescending(x => x.DemandScore)
            .ThenByDescending(x => x.EstimatedNetProfitMxn)
            .ToList();

        var restock = await _radarService.GetRestockSuggestionsAsync(organizationId, cancellationToken);

        var hasHistory = await _dbContext.MarketRankingSnapshots
            .AsNoTracking()
            .AnyAsync(x => x.OrganizationId == organizationId, cancellationToken);

        var model = new PurchasingIndexViewModel
        {
            Tab = tab is not null && Tabs.Contains(tab) ? tab.ToLowerInvariant() : "buy",
            ToBuy = toBuy,
            ToReview = candidates
                .Where(x => (x.Status == PurchaseCandidateStatus.NoSource ||
                             x.Status == PurchaseCandidateStatus.LowMargin) &&
                            x.DemandScore > 0)
                .OrderByDescending(x => x.DemandScore)
                .ToList(),
            InTransit = candidates
                .Where(x => x.Status == PurchaseCandidateStatus.Purchased)
                .OrderBy(x => x.PurchasedAt)
                .ToList(),
            Discarded = candidates
                .Where(x => x.Status == PurchaseCandidateStatus.Discarded)
                .OrderByDescending(x => x.UpdatedAt)
                .Take(100)
                .ToList(),
            Restock = restock,
            ShoppingListUnits = toBuy.Sum(x => x.SuggestedQuantity),
            ShoppingListTotalUsd = toBuy.Sum(x =>
                ((x.SourcePriceUsd ?? 0m) + (x.SourceShippingUsd ?? 0m)) * x.SuggestedQuantity),
            ShoppingListExpectedProfitMxn = toBuy.Sum(x => (x.EstimatedNetProfitMxn ?? 0m) * x.SuggestedQuantity),
            UsdToMxnRate = settings.UsdToMxnRate,
            MinProfitAmountMxn = settings.MinProfitAmountMxn,
            LastEvaluatedAt = candidates.Count > 0 ? candidates.Max(x => x.LastEvaluatedAt) : null,
            HasRankingHistory = hasHistory
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(bool force, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);

        try
        {
            var result = await _radarService.RefreshAsync(organizationId, force, cancellationToken);

            if (result.CandidatesEvaluated == 0 && result.Warning is not null)
            {
                TempData["Error"] = result.Warning;
            }
            else
            {
                TempData["Success"] =
                    $"Radar actualizado: {result.CandidatesEvaluated} productos con demanda, " +
                    $"{result.SourcesSearched} búsquedas en eBay, {result.SourcesMatched} coincidencias y " +
                    $"{result.Profitable} listos para comprar.";
                if (result.Warning is not null)
                    TempData["Warning"] = result.Warning;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Purchase radar refresh failed for {OrganizationId}.", organizationId);
            TempData["Error"] = "No fue posible actualizar el radar. Intenta de nuevo en unos minutos.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPurchased(
        long id,
        int quantity,
        string? unitCostUsd,
        CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);

        decimal? cost = null;
        if (!string.IsNullOrWhiteSpace(unitCostUsd))
        {
            if (!decimal.TryParse(unitCostUsd.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                TempData["Error"] = "El costo unitario no es un número válido.";
                return RedirectToAction(nameof(Index));
            }

            cost = parsed;
        }

        try
        {
            var confirmation = await _radarService.MarkPurchasedAsync(
                organizationId,
                id,
                quantity,
                cost,
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                cancellationToken);

            TempData["Success"] =
                $"Compra registrada ({quantity} pzs). Se creó el producto con su SKU y un borrador de publicación. " +
                "Cuando lleguen a Juárez márcalo como recibido para sumar el stock.";
            TempData["PublishProductId"] = confirmation.ProductId;
            if (confirmation.MarketplaceAccountId.HasValue)
                TempData["PublishAccountId"] = confirmation.MarketplaceAccountId.Value;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { tab = "transit" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReceived(long id, int? quantity, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);

        try
        {
            await _radarService.MarkReceivedAsync(
                organizationId,
                id,
                quantity,
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                cancellationToken);
            TempData["Success"] = "Mercancía recibida: el stock ya está disponible en Inventario.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { tab = "transit" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetSource(
        long id,
        string sourceUrl,
        string? priceUsd,
        string? shippingUsd,
        string? tab,
        CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);

        if (!TryParseUsd(priceUsd, out var price) || !TryParseUsd(shippingUsd, out var shipping))
        {
            TempData["Error"] = "El precio y el envío deben ser números, por ejemplo 38.50.";
            return RedirectToAction(nameof(Index), new { tab });
        }

        try
        {
            await _radarService.SetSourceManuallyAsync(organizationId, id, sourceUrl, price, shipping, cancellationToken);
            TempData["Success"] = "Oferta vinculada y margen recalculado.";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Manual source link failed for candidate {Id}.", id);
            TempData["Error"] = ex is ArgumentException or InvalidOperationException or KeyNotFoundException
                ? ex.Message
                : "No fue posible leer ese artículo. Verifica el link o escribe el precio manualmente.";
        }

        return RedirectToAction(nameof(Index), new { tab });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Discard(long id, string? tab, CancellationToken cancellationToken)
    {
        await UpdateStatusAsync(id, PurchaseCandidateStatus.Discarded, cancellationToken);
        return RedirectToAction(nameof(Index), new { tab });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(long id, CancellationToken cancellationToken)
    {
        // Vuelve a "sin fuente" para que la próxima actualización lo reevalúe completo.
        if (await UpdateStatusAsync(id, PurchaseCandidateStatus.NoSource, cancellationToken, resetSearch: true))
            TempData["Success"] = "Producto restaurado. Se reevaluará en la próxima actualización del radar.";
        return RedirectToAction(nameof(Index), new { tab = "discarded" });
    }

    /// <summary>Lista de compras para llevar al Paso (CSV compatible con Excel).</summary>
    [HttpGet]
    public async Task<IActionResult> ExportShoppingList(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);

        var toBuy = await _dbContext.PurchaseCandidates
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.Status == PurchaseCandidateStatus.Suggested &&
                        x.DemandScore > 0 &&
                        x.SuggestedQuantity > 0)
            .OrderByDescending(x => x.DemandScore)
            .ToListAsync(cancellationToken);

        var restock = await _radarService.GetRestockSuggestionsAsync(organizationId, cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",",
            "Tipo", "Producto", "Fuente", "Link de compra", "Precio USD", "Envio USD",
            "Cantidad", "Total USD", "Precio venta MXN", "Utilidad por pieza MXN", "Demanda"));

        foreach (var x in toBuy)
        {
            var unit = (x.SourcePriceUsd ?? 0m) + (x.SourceShippingUsd ?? 0m);
            csv.AppendLine(string.Join(",",
                Csv("Nuevo"),
                Csv(x.MarketName),
                Csv(x.SourceMarketplace),
                Csv(x.SourceUrl),
                Num(x.SourcePriceUsd),
                Num(x.SourceShippingUsd),
                x.SuggestedQuantity.ToString(CultureInfo.InvariantCulture),
                Num(unit * x.SuggestedQuantity),
                Num(x.RecommendedListingPriceMxn),
                Num(x.EstimatedNetProfitMxn),
                x.DemandScore.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var x in restock)
        {
            var isUsd = !string.Equals(x.SourceCurrency, "MXN", StringComparison.OrdinalIgnoreCase);
            csv.AppendLine(string.Join(",",
                Csv("Volver a comprar"),
                Csv($"{x.Title} ({x.Sku})"),
                Csv(x.SourceMarketplace),
                Csv(x.SourceUrl),
                isUsd ? Num(x.SourcePrice) : string.Empty,
                string.Empty,
                x.SuggestedQuantity.ToString(CultureInfo.InvariantCulture),
                isUsd && x.SourcePrice.HasValue ? Num(x.SourcePrice * x.SuggestedQuantity) : string.Empty,
                Num(x.AverageSalePriceMxn),
                string.Empty,
                Csv($"{x.UnitsSold30Days} vendidos en 30 días")));
        }

        // BOM para que Excel respete acentos.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        var fileName = $"lista-compras-{DateTime.Now:yyyy-MM-dd}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private async Task<bool> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken,
        bool resetSearch = false)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var candidate = await _dbContext.PurchaseCandidates
            .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId, cancellationToken);

        if (candidate is null)
        {
            TempData["Error"] = "No se encontró el producto del radar.";
            return false;
        }

        if (candidate.Status is PurchaseCandidateStatus.Purchased or PurchaseCandidateStatus.Received)
        {
            TempData["Error"] = "Este producto ya se compró; no se puede descartar desde el radar.";
            return false;
        }

        candidate.Status = status;
        candidate.SuggestedQuantity = 0;
        if (resetSearch)
            candidate.LastSourceSearchAt = null;
        candidate.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool TryParseUsd(string? value, out decimal? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var clean = value.Replace("$", string.Empty).Replace("US", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(",", ".").Trim();
        if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return false;

        result = parsed;
        return true;
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Evita inyección de fórmulas al abrir el CSV en Excel.
        if (value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Num(decimal? value) =>
        value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty;
}
