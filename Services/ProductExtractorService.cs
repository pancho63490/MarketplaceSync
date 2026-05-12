using MarketplaceSync.Web.ViewModels;
using System.Text.RegularExpressions;

namespace MarketplaceSync.Web.Services
{
    public class ProductExtractorService
    {
        private readonly MarketplaceDetectorService _detector;
        private readonly EbayApiService _ebayApiService;

        public ProductExtractorService(
            MarketplaceDetectorService detector,
            EbayApiService ebayApiService)
        {
            _detector = detector;
            _ebayApiService = ebayApiService;
        }

        public async Task<ExtractedProductInfo> ExtractAsync(string url)
        {
            var marketplace = _detector.DetectMarketplace(url);
            var productId = ExtractProductId(url, marketplace);

            if (marketplace.Equals("eBay", StringComparison.OrdinalIgnoreCase) ||
                marketplace.Equals("EBAY", StringComparison.OrdinalIgnoreCase))
            {
                var ebayResult = await _ebayApiService.ExtractFromUrlOrSearchAsync(url);

                if (string.IsNullOrWhiteSpace(ebayResult.SourceProductId))
                {
                    ebayResult.SourceProductId = productId;
                }

                if (string.IsNullOrWhiteSpace(ebayResult.SourceUrl))
                {
                    ebayResult.SourceUrl = url;
                }

                ebayResult.SourceMarketplace = "eBay";

                return ebayResult;
            }

            var result = new ExtractedProductInfo
            {
                SourceUrl = url,
                SourceMarketplace = marketplace,
                SourceProductId = productId,
                SourceCurrency = "MXN",
                SourceStock = 1,
                SourceStatus = "Detected"
            };

            if (marketplace.Equals("Amazon", StringComparison.OrdinalIgnoreCase))
            {
                result.Title = "Producto Amazon pendiente de extracción";
                result.Description = "Producto detectado desde Amazon. Pendiente conectar API oficial.";
            }
            else if (marketplace.Equals("MercadoLibre", StringComparison.OrdinalIgnoreCase))
            {
                result.Title = "Producto Mercado Libre detectado";
                result.Description = "Producto detectado desde Mercado Libre.";
            }
            else
            {
                result.Title = "Producto detectado";
                result.Description = "Marketplace detectado, pendiente de extracción.";
            }

            return result;
        }

        private string? ExtractProductId(string url, string marketplace)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (marketplace.Equals("Amazon", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(
                    url,
                    @"(?:/dp/|/gp/product/)([A-Z0-9]{10})",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                    return match.Groups[1].Value;
            }

            if (marketplace.Equals("eBay", StringComparison.OrdinalIgnoreCase) ||
                marketplace.Equals("EBAY", StringComparison.OrdinalIgnoreCase))
            {
                var patterns = new[]
                {
                    @"/itm/(?:.*?/)?(\d{9,15})",
                    @"[?&]itm=(\d{9,15})",
                    @"[?&]item=(\d{9,15})"
                };

                foreach (var pattern in patterns)
                {
                    var match = Regex.Match(url, pattern, RegexOptions.IgnoreCase);

                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }

            if (marketplace.Equals("MercadoLibre", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(
                    url,
                    @"(MLM[-]?\d+)",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                    return match.Groups[1].Value.Replace("-", "").ToUpper();
            }

            return null;
        }
    }
}