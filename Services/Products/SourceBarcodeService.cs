using System.Text.RegularExpressions;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Services;
using MarketplaceSync.Web.ViewModels;

namespace MarketplaceSync.Services.Products;

/// <summary>
/// Código de barras del producto tomado del anuncio donde se compra (eBay con su API oficial).
/// Solo copia un código real y válido; nunca genera uno. Se busca una vez por producto.
/// </summary>
public sealed class SourceBarcodeService
{
    private static readonly Regex EbayItemIdInUrl = new(@"/itm/(?:[^/?#]+/)?(\d{9,15})", RegexOptions.IgnoreCase);

    private readonly EbayApiService _ebay;
    private readonly ILogger<SourceBarcodeService> _logger;

    public SourceBarcodeService(EbayApiService ebay, ILogger<SourceBarcodeService> logger)
    {
        _ebay = ebay;
        _logger = logger;
    }

    /// <summary>true si la tienda sí entregó sus datos (aunque no trajera código): ya no hay que volver a buscar.</summary>
    public static bool WasRead(ExtractedProductInfo extracted) =>
        extracted.Gtin is not null ||
        extracted.SourceStatus is "ExtractedByEbayApi" or "Scraped";

    /// <summary>
    /// Regresa el código de barras del anuncio de origen. Si el producto es de eBay y nunca se buscó,
    /// lo consulta y lo deja en <see cref="Product.SourceGtin"/> (el llamador guarda los cambios).
    /// </summary>
    public async Task<string?> EnsureAsync(Product product, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(product.SourceGtin) || product.SourceGtinCheckedAt is not null)
            return product.SourceGtin;

        var legacyId = EbayLegacyId(product);
        if (legacyId is null || !_ebay.IsConfigured)
            return null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await _ebay.GetItemByLegacyIdAsync(legacyId, product.SourceUrl);
            product.SourceGtin = item.Gtin;
            product.SourceGtinOrigin = item.Gtin is null ? null : "eBay";
            product.SourceGtinCheckedAt = DateTime.UtcNow;
            return item.Gtin;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Sin marcar como revisado: se vuelve a intentar la próxima vez.
            _logger.LogWarning(exception, "Could not read the source barcode for product {ProductId}.", product.Id);
            return null;
        }
    }

    private static string? EbayLegacyId(Product product)
    {
        var isEbay = string.Equals(product.SourceMarketplace, "eBay", StringComparison.OrdinalIgnoreCase) ||
                     product.SourceUrl.Contains("ebay.", StringComparison.OrdinalIgnoreCase);
        if (!isEbay)
            return null;

        if (!string.IsNullOrWhiteSpace(product.SourceProductId) && product.SourceProductId.All(char.IsDigit))
            return product.SourceProductId;

        // "v1|123456789012|0" o el número en el link.
        var parts = product.SourceProductId?.Split('|');
        if (parts is { Length: >= 2 } && parts[1].All(char.IsDigit) && parts[1].Length > 0)
            return parts[1];

        var match = EbayItemIdInUrl.Match(product.SourceUrl);
        return match.Success ? match.Groups[1].Value : null;
    }
}
