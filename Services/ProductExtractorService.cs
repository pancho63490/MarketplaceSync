using MarketplaceSync.Web.ViewModels;
using System.Text.RegularExpressions;

namespace MarketplaceSync.Web.Services
{
    public class ProductExtractorService
    {
        private readonly MarketplaceDetectorService _detector;

        public ProductExtractorService(MarketplaceDetectorService detector)
        {
            _detector = detector;
        }

        public Task<ExtractedProductInfo> ExtractAsync(string url)
        {
            var marketplace = _detector.DetectMarketplace(url);
            var productId = ExtractProductId(url, marketplace);

            var result = new ExtractedProductInfo
            {
                SourceUrl = url,
                SourceMarketplace = marketplace,
                SourceProductId = productId,
                SourceCurrency = "MXN",
                SourceStock = 1,
                SourceStatus = "Detected"
            };

            if (marketplace == "Amazon")
            {
                result.Title = "Producto Amazon pendiente de extracción";
                result.Description = "Producto detectado desde Amazon. Pendiente conectar API o extractor.";
            }
            else if (marketplace == "eBay")
            {
                result.Title = "Producto eBay pendiente de extracción";
                result.Description = "Producto detectado desde eBay. Pendiente conectar API o extractor.";
            }
            else if (marketplace == "MercadoLibre")
            {
                result.Title = "Producto Mercado Libre detectado";
                result.Description = "Producto detectado desde Mercado Libre.";
            }
            else
            {
                result.Title = "Producto detectado";
                result.Description = "Marketplace detectado, pendiente de extracción.";
            }

            return Task.FromResult(result);
        }

        private string? ExtractProductId(string url, string marketplace)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (marketplace == "Amazon")
            {
                var match = Regex.Match(url, @"(?:/dp/|/gp/product/)([A-Z0-9]{10})", RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups[1].Value;
            }

            if (marketplace == "eBay")
            {
                var match = Regex.Match(url, @"/itm/(?:.*?/)?(\d{9,15})", RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups[1].Value;
            }

            if (marketplace == "MercadoLibre")
            {
                var match = Regex.Match(url, @"(MLM[-]?\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups[1].Value.Replace("-", "").ToUpper();
            }

            return null;
        }
    }
}