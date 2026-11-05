namespace MarketplaceSync.Web.Services
{
    public class MarketplaceDetectorService
    {
        public string DetectMarketplace(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "UNKNOWN";

            if (url.Contains("amazon.", StringComparison.OrdinalIgnoreCase))
                return "AMAZON";

            if (url.Contains("ebay.", StringComparison.OrdinalIgnoreCase))
                return "EBAY";

            if (url.Contains("mercadolibre.", StringComparison.OrdinalIgnoreCase))
                return "MERCADOLIBRE";

            if (url.Contains("aliexpress.", StringComparison.OrdinalIgnoreCase))
                return "ALIEXPRESS";

            if (url.Contains("walmart.com", StringComparison.OrdinalIgnoreCase))
                return "WALMART";

            return "UNKNOWN";
        }

        public string? ExtractSourceProductId(string url)
        {
            var marketplace = DetectMarketplace(url);

            return marketplace switch
            {
                "AMAZON" => ExtractAmazonAsin(url),
                "EBAY" => ExtractEbayItemId(url),
                "MERCADOLIBRE" => ExtractMercadoLibreItemId(url),
                "ALIEXPRESS" => ExtractAliExpressItemId(url),
                "WALMART" => System.Text.RegularExpressions.Regex.Match(url, @"/ip/(?:[^/?#]+/)?(\d{5,})").Groups[1].Value is { Length: > 0 } id ? id : null,
                _ => null
            };
        }

        private string? ExtractAmazonAsin(string url)
        {
            var patterns = new[]
            {
                @"/dp/([A-Z0-9]{10})",
                @"/gp/product/([A-Z0-9]{10})",
                @"asin=([A-Z0-9]{10})"
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    url,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                if (match.Success)
                    return match.Groups[1].Value.ToUpper();
            }

            return null;
        }

        private string? ExtractEbayItemId(string url)
        {
            var patterns = new[]
            {
                @"/itm/(\d+)",
                @"item=(\d+)"
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    url,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                if (match.Success)
                    return match.Groups[1].Value;
            }

            return null;
        }

        private string? ExtractMercadoLibreItemId(string url)
        {
            var patterns = new[]
            {
                @"(MLM-\d+)",
                @"(MLM\d+)"
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    url,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                if (match.Success)
                    return match.Groups[1].Value.Replace("-", "").ToUpper();
            }

            return null;
        }

        private string? ExtractAliExpressItemId(string url)
        {
            var patterns = new[]
            {
                @"/item/(\d+)\.html",
                @"/i/(\d+)\.html"
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    url,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );

                if (match.Success)
                    return match.Groups[1].Value;
            }

            return null;
        }
    }
}