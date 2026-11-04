using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Monitor;

/// <summary>
/// Revisa precio y disponibilidad en la fuente (eBay/Amazon/AliExpress) de los productos
/// rastreados. Recalcula el precio con CrossBorderPricingService cuando el cambio es
/// significativo y sincroniza Mercado Libre.
///
/// Reglas de seguridad:
/// - Los productos se procesan en secuencia: AppDbContext no admite operaciones concurrentes.
/// - Si el producto tiene inventario propio (variantes con stock), la fuente agotada NO pausa
///   la publicación: ese stock ya está físicamente en Juárez.
/// - Al reactivar una publicación se usa una cantidad conservadora para no sobrevender.
/// - El precio de publicaciones con variaciones no se modifica aquí (Mercado Libre exige
///   actualizar cada variación); se registra en logs para revisión.
/// - El estado local solo cambia cuando Mercado Libre confirma la actualización.
/// </summary>
public sealed class SourcePriceMonitorService : ISourcePriceMonitorService
{
    private const decimal PriceChangeTriggerThreshold = 0.03m; // 3 %
    private const int MaxRestoreQuantity = 5;
    private static readonly TimeSpan DelayBetweenProducts = TimeSpan.FromMilliseconds(750);
    private static readonly string[] MonitoredMarketplaces = { "EBAY", "AMAZON", "ALIEXPRESS" };

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProductExtractorService _extractor;
    private readonly ICrossBorderPricingService _crossBorderPricingService;
    private readonly IMercadoLibreService _mercadoLibreService;
    private readonly IMercadoLibreAuthService _mercadoLibreAuthService;
    private readonly ILogger<SourcePriceMonitorService> _logger;

    public SourcePriceMonitorService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        ProductExtractorService extractor,
        ICrossBorderPricingService crossBorderPricingService,
        IMercadoLibreService mercadoLibreService,
        IMercadoLibreAuthService mercadoLibreAuthService,
        ILogger<SourcePriceMonitorService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _extractor = extractor;
        _crossBorderPricingService = crossBorderPricingService;
        _mercadoLibreService = mercadoLibreService;
        _mercadoLibreAuthService = mercadoLibreAuthService;
        _logger = logger;
    }

    public async Task MonitorAllProductsAsync(CancellationToken cancellationToken = default)
    {
        var productIds = await _context.Products
            .AsNoTracking()
            .Where(p =>
                MonitoredMarketplaces.Contains(p.SourceMarketplace.ToUpper()) &&
                p.SourceUrl != "" &&
                p.Status != "Archived")
            .OrderBy(p => p.LastSourceCheckAt ?? DateTime.MinValue)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("SourcePriceMonitor: checking {Count} products.", productIds.Count);

        int stockChanged = 0, priceChanged = 0, errors = 0;

        foreach (var productId in productIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await CheckProductAsync(productId, cancellationToken);
            if (result.StockChanged) stockChanged++;
            if (result.PriceChanged) priceChanged++;
            if (result.Outcome.StartsWith("Error", StringComparison.Ordinal)) errors++;

            // Libera el tracking para que el job no acumule memoria en catálogos grandes.
            _context.ChangeTracker.Clear();
            await Task.Delay(DelayBetweenProducts, cancellationToken);
        }

        _logger.LogInformation(
            "SourcePriceMonitor: done. StockChanged={StockChanged}, PriceChanged={PriceChanged}, Errors={Errors}",
            stockChanged, priceChanged, errors);
    }

    public async Task<SourceCheckResult> CheckProductAsync(
        int productId,
        CancellationToken cancellationToken = default)
    {
        var product = await _context.Products
            .Include(p => p.Variants)
            .Include(p => p.MarketplacePublications)
                .ThenInclude(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

        if (product is null)
            return new SourceCheckResult(productId, false, false, null, null, null, "ProductNotFound");

        return await CheckAndUpdateProductAsync(product, cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────
    // Lógica por producto
    // ─────────────────────────────────────────────────────────────

    private async Task<SourceCheckResult> CheckAndUpdateProductAsync(
        Product product,
        CancellationToken cancellationToken)
    {
        try
        {
            var freshData = await _extractor.ExtractAsync(product.SourceUrl);

            var stockChanged = false;
            var priceChanged = false;
            decimal? newListingPrice = null;
            var hasOwnInventory = product.Variants.Any(v => v.IsActive && v.Stock > 0);

            // ── Disponibilidad ───────────────────────────────────
            int? newStock = freshData.SourceStock;
            var wasInStock = (product.SourceStock ?? 1) > 0;
            var nowInStock = (newStock ?? 1) > 0;

            product.SourceStock = newStock;
            product.SourceAvailabilityText = freshData.SourceAvailabilityText;

            if (wasInStock != nowInStock)
            {
                stockChanged = true;
                _logger.LogInformation(
                    "Product {Id} source availability changed: {Old} → {New}",
                    product.Id, wasInStock, nowInStock);

                if (!nowInStock && !hasOwnInventory)
                {
                    product.Status = "OutOfStock";
                    await PauseMercadoLibrePublicationsAsync(product, cancellationToken);
                }
                else if (nowInStock && product.Status == "OutOfStock")
                {
                    product.Status = "ReadyToPublish";
                    await RestoreMercadoLibrePublicationsAsync(product, cancellationToken);
                }
            }

            // ── Precio ───────────────────────────────────────────
            var oldPrice = product.SourcePrice;
            var newSourcePrice = freshData.SourcePrice;

            if (newSourcePrice is > 0)
            {
                var hasPrevious = oldPrice is > 0;
                var changeFraction = hasPrevious
                    ? Math.Abs((newSourcePrice.Value - oldPrice!.Value) / oldPrice.Value)
                    : 0m;

                product.SourcePrice = newSourcePrice;
                product.SourceCurrency = freshData.SourceCurrency ?? product.SourceCurrency;

                if (hasPrevious && changeFraction >= PriceChangeTriggerThreshold)
                {
                    priceChanged = true;
                    _logger.LogInformation(
                        "Product {Id} source price changed: {Old} → {New} ({Pct:P1})",
                        product.Id, oldPrice, newSourcePrice, changeFraction);

                    // Con inventario propio el costo ya se pagó; el precio de la fuente
                    // solo sirve como referencia para la siguiente compra.
                    if (!hasOwnInventory)
                    {
                        newListingPrice = await RecalculateListingPriceAsync(
                            product, newSourcePrice.Value, cancellationToken);

                        if (newListingPrice.HasValue)
                        {
                            await UpdateMercadoLibrePriceAsync(
                                product, newListingPrice.Value, cancellationToken);
                        }
                    }
                }
            }

            product.LastSourceCheckAt = DateTime.UtcNow;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            return new SourceCheckResult(
                product.Id,
                stockChanged,
                priceChanged,
                newStock,
                newSourcePrice,
                newListingPrice,
                "OK");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to check product {Id} source.", product.Id);

            product.LastErrorMessage = Truncate("Monitor: " + ex.Message, 1000);
            product.LastErrorAt = DateTime.UtcNow;
            product.LastSourceCheckAt = DateTime.UtcNow;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception saveEx)
            {
                _logger.LogWarning(saveEx, "Could not persist monitor error for product {Id}.", product.Id);
            }

            return new SourceCheckResult(product.Id, false, false, null, null, null, "Error:" + ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Recalcular precio
    // ─────────────────────────────────────────────────────────────

    private async Task<decimal?> RecalculateListingPriceAsync(
        Product product,
        decimal newSourcePrice,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _crossBorderPricingService
                .GetOrCreateSettingsAsync(product.OrganizationId, cancellationToken);

            var sourceIsMxn = string.Equals(product.SourceCurrency, "MXN", StringComparison.OrdinalIgnoreCase);

            var breakdown = sourceIsMxn
                ? _crossBorderPricingService.CalculatePricingFromMxn(newSourcePrice, settings)
                : _crossBorderPricingService.CalculatePricing(newSourcePrice, settings);

            return breakdown.IsProfitable ? breakdown.SuggestedListingPriceMxn : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not recalculate price for product {Id}.", product.Id);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Mercado Libre
    // ─────────────────────────────────────────────────────────────

    private static IEnumerable<MarketplacePublication> MercadoLibrePublications(Product product) =>
        product.MarketplacePublications.Where(p =>
            p.Marketplace == "MercadoLibre" &&
            !string.IsNullOrWhiteSpace(p.ExternalItemId));

    private async Task PauseMercadoLibrePublicationsAsync(Product product, CancellationToken cancellationToken)
    {
        foreach (var pub in MercadoLibrePublications(product).Where(p => p.IsPublished && p.Status != "paused"))
        {
            var ok = await PutMercadoLibreItemAsync(
                product.OrganizationId, pub, new { status = "paused" }, cancellationToken);

            if (ok)
            {
                pub.Status = "paused";
                pub.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    private async Task RestoreMercadoLibrePublicationsAsync(Product product, CancellationToken cancellationToken)
    {
        var restoreQty = Math.Clamp(product.SourceStock ?? 1, 1, MaxRestoreQuantity);

        foreach (var pub in MercadoLibrePublications(product).Where(p => p.Status == "paused"))
        {
            object body = pub.Variants.Count > 0
                ? new { status = "active" }
                : new { status = "active", available_quantity = restoreQty };

            var ok = await PutMercadoLibreItemAsync(product.OrganizationId, pub, body, cancellationToken);

            if (ok)
            {
                if (pub.Variants.Count == 0)
                    pub.Stock = restoreQty;

                pub.Status = "active";
                pub.IsPublished = true;
                pub.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    private async Task UpdateMercadoLibrePriceAsync(
        Product product,
        decimal newPriceMxn,
        CancellationToken cancellationToken)
    {
        foreach (var pub in MercadoLibrePublications(product).Where(p => p.IsPublished))
        {
            if (pub.Variants.Count > 0)
            {
                _logger.LogInformation(
                    "Skipping automatic price update for item {ItemId}: publication has variations. Suggested price {Price}.",
                    pub.ExternalItemId, newPriceMxn);
                continue;
            }

            var rounded = Math.Round(newPriceMxn, 2);
            var ok = await PutMercadoLibreItemAsync(
                product.OrganizationId, pub, new { price = rounded }, cancellationToken);

            if (ok)
            {
                pub.Price = rounded;
                pub.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    private async Task<bool> PutMercadoLibreItemAsync(
        Guid organizationId,
        MarketplacePublication publication,
        object body,
        CancellationToken cancellationToken)
    {
        var externalItemId = publication.ExternalItemId!;
        try
        {
            var accessToken = publication.MarketplaceAccountId.HasValue
                ? await _mercadoLibreAuthService.GetValidAccessTokenAsync(
                    organizationId, publication.MarketplaceAccountId.Value)
                : await _mercadoLibreService.GetAccessTokenAsync(organizationId);

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"https://api.mercadolibre.com/items/{Uri.EscapeDataString(externalItemId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "MeLi PUT {ItemId} returned {Status}: {Body}",
                    externalItemId, (int)response.StatusCode, Truncate(error, 1000));
                return false;
            }

            _logger.LogInformation("MeLi item {ItemId} updated successfully.", externalItemId);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to update MeLi item {ItemId}.", externalItemId);
            return false;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
