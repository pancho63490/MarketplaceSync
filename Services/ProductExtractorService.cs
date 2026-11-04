using MarketplaceSync.Web.ViewModels;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MarketplaceSync.Web.Services
{
    public class ProductExtractorService
    {
        private readonly MarketplaceDetectorService _detector;
        private readonly EbayApiService _ebayApiService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ProductExtractorService> _logger;

        public ProductExtractorService(
            MarketplaceDetectorService detector,
            EbayApiService ebayApiService,
            IHttpClientFactory httpClientFactory,
            ILogger<ProductExtractorService> logger)
        {
            _detector = detector;
            _ebayApiService = ebayApiService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
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

            if (marketplace.Equals("AMAZON", StringComparison.OrdinalIgnoreCase))
            {
                return await ScrapeAmazonAsync(url, productId);
            }

            if (marketplace.Equals("ALIEXPRESS", StringComparison.OrdinalIgnoreCase))
            {
                return await ScrapeAliExpressAsync(url, productId);
            }

            // Universal fallback for any unrecognized marketplace
            return await ScrapeUniversalAsync(url, marketplace, productId);
        }

        // ─── Amazon ───────────────────────────────────────────────────────────────

        private async Task<ExtractedProductInfo> ScrapeAmazonAsync(string url, string? productId)
        {
            var partial = new ExtractedProductInfo
            {
                SourceUrl = url,
                SourceMarketplace = "Amazon",
                SourceProductId = productId,
                SourceCurrency = "USD",
                SourceStock = 1,
                Title = productId != null ? $"Amazon: {productId}" : "Amazon Product",
                SourceStatus = "ScrapeFailed"
            };

            try
            {
                var html = await FetchHtmlAsync(url);
                if (string.IsNullOrWhiteSpace(html))
                    return partial;

                // Title: <span id="productTitle"> or og:title
                var title = ExtractByIdSpan(html, "productTitle")
                            ?? ExtractMetaContent(html, "property", "og:title");

                // Price: a-price-whole + a-price-fraction
                string? price = null;
                var priceWhole = ExtractByClassName(html, "a-price-whole");
                var priceFraction = ExtractByClassName(html, "a-price-fraction");
                if (!string.IsNullOrWhiteSpace(priceWhole))
                {
                    // Strip any non-digit/dot from whole part
                    priceWhole = Regex.Replace(priceWhole, @"[^\d]", "");
                    priceFraction = string.IsNullOrWhiteSpace(priceFraction)
                        ? "00"
                        : Regex.Replace(priceFraction, @"[^\d]", "");
                    price = $"{priceWhole}.{priceFraction}";
                }
                price ??= ExtractMetaContent(html, "property", "og:price:amount");

                // Image: og:image or img id="landingImage"
                var image = ExtractMetaContent(html, "property", "og:image")
                            ?? ExtractImgSrcById(html, "landingImage");

                // Description: meta description or feature-bullets li items
                var description = ExtractMetaContent(html, "name", "description");
                if (string.IsNullOrWhiteSpace(description))
                {
                    description = ExtractFeatureBullets(html);
                }
                if (!string.IsNullOrWhiteSpace(description) && description.Length > 1000)
                    description = description[..1000];

                // Brand: <a id="bylineInfo"> or Brand table row
                var brand = ExtractByIdAnchor(html, "bylineInfo")
                            ?? ExtractBrandFromTable(html);

                return new ExtractedProductInfo
                {
                    SourceUrl = url,
                    SourceMarketplace = "Amazon",
                    SourceProductId = productId,
                    SourceCurrency = "USD",
                    SourceStock = 1,
                    SourceStatus = "Scraped",
                    Title = title ?? partial.Title,
                    Description = description,
                    ImageUrl = image,
                    SourcePrice = TryParseDecimal(price),
                    Brand = CleanText(brand)
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Amazon scraping failed for URL: {Url}", url);
                return partial;
            }
        }

        // ─── AliExpress ───────────────────────────────────────────────────────────

        private async Task<ExtractedProductInfo> ScrapeAliExpressAsync(string url, string? productId)
        {
            var partial = new ExtractedProductInfo
            {
                SourceUrl = url,
                SourceMarketplace = "AliExpress",
                SourceProductId = productId,
                SourceCurrency = "USD",
                SourceStock = 1,
                Title = productId != null ? $"AliExpress: {productId}" : "AliExpress Product",
                SourceStatus = "ScrapeFailed"
            };

            try
            {
                var html = await FetchHtmlAsync(url);
                if (string.IsNullOrWhiteSpace(html))
                    return partial;

                // Title: og:title (strip " - AliExpress" suffix), fallback <title>
                var title = ExtractMetaContent(html, "property", "og:title")
                            ?? ExtractTitleTag(html);
                if (!string.IsNullOrWhiteSpace(title))
                    title = Regex.Replace(title, @"\s*-\s*AliExpress\s*$", "", RegexOptions.IgnoreCase).Trim();

                // Price: og:price:amount, fallback inline JSON "originalPrice":
                var price = ExtractMetaContent(html, "property", "og:price:amount");
                if (string.IsNullOrWhiteSpace(price))
                {
                    var priceMatch = Regex.Match(html, @"""originalPrice""\s*:\s*""?([0-9]+(?:\.[0-9]+)?)""?");
                    if (priceMatch.Success)
                        price = priceMatch.Groups[1].Value;
                }

                // Currency: og:price:currency or default USD
                var currency = ExtractMetaContent(html, "property", "og:price:currency") ?? "USD";

                // Image: og:image
                var image = ExtractMetaContent(html, "property", "og:image");

                // Description: meta description
                var description = ExtractMetaContent(html, "name", "description");
                if (!string.IsNullOrWhiteSpace(description) && description.Length > 1000)
                    description = description[..1000];

                // Brand: JSON-LD Brand field
                var brand = ExtractJsonLdBrand(html);

                // Product ID from URL if not already set
                productId ??= ExtractAliExpressProductId(url);

                return new ExtractedProductInfo
                {
                    SourceUrl = url,
                    SourceMarketplace = "AliExpress",
                    SourceProductId = productId,
                    SourceCurrency = currency,
                    SourceStock = 1,
                    SourceStatus = "Scraped",
                    Title = title ?? partial.Title,
                    Description = description,
                    ImageUrl = image,
                    SourcePrice = TryParseDecimal(price),
                    Brand = brand
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AliExpress scraping failed for URL: {Url}", url);
                return partial;
            }
        }

        // ─── Universal fallback ───────────────────────────────────────────────────

        private async Task<ExtractedProductInfo> ScrapeUniversalAsync(string url, string marketplace, string? productId)
        {
            var result = new ExtractedProductInfo
            {
                SourceUrl = url,
                SourceMarketplace = marketplace,
                SourceProductId = productId,
                SourceCurrency = "USD",
                SourceStock = 1,
                SourceStatus = "ScrapeFailed",
                Title = "Product"
            };

            try
            {
                var html = await FetchHtmlAsync(url);
                if (string.IsNullOrWhiteSpace(html))
                    return result;

                var title = ExtractMetaContent(html, "property", "og:title")
                            ?? ExtractTitleTag(html);
                var description = ExtractMetaContent(html, "property", "og:description")
                                  ?? ExtractMetaContent(html, "name", "description");
                var image = ExtractMetaContent(html, "property", "og:image");
                var price = ExtractMetaContent(html, "property", "og:price:amount");
                var currency = ExtractMetaContent(html, "property", "og:price:currency") ?? "USD";

                if (!string.IsNullOrWhiteSpace(description) && description.Length > 1000)
                    description = description[..1000];

                result.Title = title ?? result.Title;
                result.Description = description;
                result.ImageUrl = image;
                result.SourcePrice = TryParseDecimal(price);
                result.SourceCurrency = currency;
                result.SourceStatus = string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(image)
                    ? "ScrapeFailed"
                    : "Scraped";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Universal scraping failed for URL: {Url}", url);
            }

            return result;
        }

        // ─── HTTP helper ──────────────────────────────────────────────────────────

        private async Task<string?> FetchHtmlAsync(string url)
        {
            var client = _httpClientFactory.CreateClient("ScraperClient");
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Scraper got HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }
            return await response.Content.ReadAsStringAsync();
        }

        // ─── HTML extraction helpers (Regex-based, no HtmlAgilityPack) ───────────

        private static string? ExtractMetaContent(string html, string attrName, string attrValue)
        {
            // <meta property="og:title" content="..."> or attributes in any order
            var pattern = $@"<meta\b[^>]*{Regex.Escape(attrName)}=""{Regex.Escape(attrValue)}""[^>]*content=""([^""]*)""|<meta\b[^>]*content=""([^""]*)""][^>]*{Regex.Escape(attrName)}=""{Regex.Escape(attrValue)}""";
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            var val = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return HtmlDecode(val);
        }

        private static string? ExtractByIdSpan(string html, string id)
        {
            var m = Regex.Match(html, $@"<span\b[^>]*\bid=""{Regex.Escape(id)}""[^>]*>(.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            return CleanText(m.Groups[1].Value);
        }

        private static string? ExtractByClassName(string html, string className)
        {
            var m = Regex.Match(html, $@"<span\b[^>]*\bclass=""[^""]*{Regex.Escape(className)}[^""]*""[^>]*>(.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            return CleanText(m.Groups[1].Value);
        }

        private static string? ExtractByIdAnchor(string html, string id)
        {
            var m = Regex.Match(html, $@"<a\b[^>]*\bid=""{Regex.Escape(id)}""[^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            return CleanText(m.Groups[1].Value);
        }

        private static string? ExtractImgSrcById(string html, string id)
        {
            var m = Regex.Match(html, $@"<img\b[^>]*\bid=""{Regex.Escape(id)}""[^>]*\bsrc=""([^""]+)""|<img\b[^>]*\bsrc=""([^""]+)""[^>]*\bid=""{Regex.Escape(id)}""",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }

        private static string? ExtractTitleTag(string html)
        {
            var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            return CleanText(m.Groups[1].Value);
        }

        private static string? ExtractFeatureBullets(string html)
        {
            // Find the feature-bullets div
            var divMatch = Regex.Match(html, @"<div\b[^>]*\bid=""feature-bullets""[^>]*>(.*?)</div>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!divMatch.Success) return null;

            var section = divMatch.Groups[1].Value;
            var liMatches = Regex.Matches(section, @"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var items = liMatches.Cast<Match>()
                .Select(m => CleanText(m.Groups[1].Value))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Take(5);
            return string.Join(". ", items);
        }

        private static string? ExtractBrandFromTable(string html)
        {
            // Look for <th> containing "Brand" followed by <td>
            var m = Regex.Match(html, @"<th[^>]*>\s*Brand\s*</th>\s*<td[^>]*>(.*?)</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            return CleanText(m.Groups[1].Value);
        }

        private static string? ExtractJsonLdBrand(string html)
        {
            // Find all <script type="application/ld+json"> blocks
            var scripts = Regex.Matches(html, @"<script\b[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match script in scripts)
            {
                var json = script.Groups[1].Value;
                var brandMatch = Regex.Match(json, @"""brand""\s*:\s*\{[^}]*""name""\s*:\s*""([^""]+)""",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (brandMatch.Success)
                    return brandMatch.Groups[1].Value;
            }
            return null;
        }

        private static string? ExtractAliExpressProductId(string url)
        {
            var m = Regex.Match(url, @"/(?:item|i)/(\d+)\.html", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string? CleanText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            // Strip HTML tags
            var stripped = Regex.Replace(input, @"<[^>]+>", " ");
            // Collapse whitespace
            stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(stripped) ? null : HtmlDecode(stripped);
        }

        private static string HtmlDecode(string input)
        {
            return System.Net.WebUtility.HtmlDecode(input);
        }

        private static decimal? TryParseDecimal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            // Keep only digits and dot
            var cleaned = Regex.Replace(value, @"[^\d.]", "");
            return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        // ─── Product ID extraction ────────────────────────────────────────────────

        private string? ExtractProductId(string url, string marketplace)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (marketplace.Equals("AMAZON", StringComparison.OrdinalIgnoreCase))
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

            if (marketplace.Equals("MercadoLibre", StringComparison.OrdinalIgnoreCase) ||
                marketplace.Equals("MERCADOLIBRE", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(
                    url,
                    @"(MLM[-]?\d+)",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                    return match.Groups[1].Value.Replace("-", "").ToUpper();
            }

            if (marketplace.Equals("ALIEXPRESS", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractAliExpressProductId(url);
            }

            return null;
        }
    }
}
