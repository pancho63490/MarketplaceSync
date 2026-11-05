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

            if (marketplace.Equals("WALMART", StringComparison.OrdinalIgnoreCase))
            {
                return await ScrapeWalmartAsync(url, _detector.ExtractSourceProductId(url));
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
                Title = TitleFromAmazonUrl(url) ?? (productId != null ? $"Amazon: {productId}" : "Amazon Product"),
                SourceStatus = "ScrapeFailed"
            };

            try
            {
                var html = await FetchHtmlAsync(url);
                if (string.IsNullOrWhiteSpace(html))
                    return partial;

                // Amazon suele responder con un CAPTCHA a los servidores: no hay datos reales que leer.
                if (html.Contains("validateCaptcha", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("Robot Check", StringComparison.OrdinalIgnoreCase))
                {
                    partial.SourceStatus = "Blocked";
                    return partial;
                }

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
                var image = ExtractBestImage(html, url);

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

                // Imagen: formatos propios de AliExpress, luego metadatos
                var image = ExtractBestImage(html, url);

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

        // ─── Walmart ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Walmart.com casi siempre responde con "Robot or human?" a los servidores. Si la página se puede leer,
        /// se toman los datos del JSON de la página (nombre, precio, foto, marca, disponibilidad);
        /// si no, el título sale del link (/ip/Nombre-Del-Producto/123) y el precio se captura a mano.
        /// </summary>
        private async Task<ExtractedProductInfo> ScrapeWalmartAsync(string url, string? productId)
        {
            var result = new ExtractedProductInfo
            {
                SourceUrl = url,
                SourceMarketplace = "Walmart",
                SourceProductId = productId,
                SourceCurrency = "USD",
                SourceStock = 1,
                Title = TitleFromWalmartUrl(url) ?? (productId != null ? $"Walmart {productId}" : "Producto de Walmart"),
                SourceStatus = "Blocked"
            };

            try
            {
                var html = await FetchHtmlAsync(url);
                if (string.IsNullOrWhiteSpace(html) ||
                    html.Contains("Robot or human", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("px-captcha", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("/blocked?", StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }

                string? name = null, brand = null, image = null, description = null, availability = null, model = null;
                decimal? price = null;

                var nextData = Regex.Match(html, @"<script[^>]*id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline);
                if (nextData.Success)
                {
                    using var document = System.Text.Json.JsonDocument.Parse(nextData.Groups[1].Value);
                    if (TryGetPath(document.RootElement, out var product, "props", "pageProps", "initialData", "data", "product"))
                    {
                        name = GetString(product, "name");
                        brand = GetString(product, "brand");
                        model = GetString(product, "model");
                        description = GetString(product, "shortDescription");
                        availability = GetString(product, "availabilityStatus");
                        if (TryGetPath(product, out var current, "priceInfo", "currentPrice") &&
                            current.TryGetProperty("price", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number)
                            price = p.GetDecimal();
                        if (TryGetPath(product, out var images, "imageInfo", "allImages") &&
                            images.ValueKind == System.Text.Json.JsonValueKind.Array && images.GetArrayLength() > 0)
                            image = GetString(images[0], "url");
                        image ??= TryGetPath(product, out var info, "imageInfo") ? GetString(info, "thumbnailUrl") : null;
                    }
                }

                // Respaldo: JSON-LD de tipo Product y metadatos sociales.
                if (name is null || price is null)
                {
                    foreach (Match script in Regex.Matches(html, @"<script\b[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    {
                        try
                        {
                            using var ld = System.Text.Json.JsonDocument.Parse(script.Groups[1].Value);
                            var root = ld.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && ld.RootElement.GetArrayLength() > 0
                                ? ld.RootElement[0]
                                : ld.RootElement;
                            if (!string.Equals(GetString(root, "@type"), "Product", StringComparison.OrdinalIgnoreCase))
                                continue;
                            name ??= GetString(root, "name");
                            description ??= GetString(root, "description");
                            if (root.TryGetProperty("brand", out var b))
                                brand ??= b.ValueKind == System.Text.Json.JsonValueKind.Object ? GetString(b, "name") : b.ValueKind == System.Text.Json.JsonValueKind.String ? b.GetString() : null;
                            if (root.TryGetProperty("image", out var img))
                                image ??= img.ValueKind == System.Text.Json.JsonValueKind.Array && img.GetArrayLength() > 0 ? img[0].GetString() : img.ValueKind == System.Text.Json.JsonValueKind.String ? img.GetString() : null;
                            if (root.TryGetProperty("offers", out var offers))
                            {
                                var offer = offers.ValueKind == System.Text.Json.JsonValueKind.Array && offers.GetArrayLength() > 0 ? offers[0] : offers;
                                price ??= TryParseDecimal(offer.TryGetProperty("price", out var op) ? op.ToString() : null);
                                availability ??= GetString(offer, "availability");
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                        }
                    }
                }

                name ??= ExtractMetaContent(html, "property", "og:title");
                image ??= ExtractBestImage(html, url);

                if (!string.IsNullOrWhiteSpace(name)) result.Title = CleanText(name) ?? result.Title;
                result.Brand = brand;
                result.Model = model;
                result.ImageUrl = string.IsNullOrWhiteSpace(image) ? null : NormalizeImageUrl(image, url);
                result.Description = CleanText(description) is { } d && d.Length > 1000 ? d[..1000] : CleanText(description);
                result.SourcePrice = price;
                if (availability is not null)
                {
                    var outOfStock = availability.Contains("OUT_OF_STOCK", StringComparison.OrdinalIgnoreCase) ||
                                     availability.Contains("OutOfStock", StringComparison.OrdinalIgnoreCase);
                    result.SourceStock = outOfStock ? 0 : 1;
                    result.SourceAvailabilityText = outOfStock ? "Agotado en Walmart" : "Disponible en Walmart";
                }
                result.SourceStatus = price is > 0 ? "Scraped" : "Blocked";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Walmart scraping failed for URL: {Url}", url);
            }

            return result;
        }

        /// <summary>"/ip/Monster-Energy-Ultra-Zero-16-oz-24-Pack/123" → "Monster Energy Ultra Zero 16 oz 24 Pack".</summary>
        public static string? TitleFromWalmartUrl(string url)
        {
            var match = Regex.Match(url, @"/ip/([^/?#]+)/\d+", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;
            var slug = Uri.UnescapeDataString(match.Groups[1].Value).Replace('-', ' ').Trim();
            return slug.Length < 3 ? null : Regex.Replace(slug, @"\s+", " ");
        }

        /// <summary>"/ACDelco-252-1047-Original-Equipment-Water/dp/B000…" → "ACDelco 252-1047 Original Equipment Water".</summary>
        public static string? TitleFromAmazonUrl(string url)
        {
            var match = Regex.Match(url, @"amazon\.[^/]+/([^/?#]+)/(?:dp|gp/product)/", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;
            var slug = Uri.UnescapeDataString(match.Groups[1].Value).Replace('-', ' ').Trim();
            return slug.Length < 3 ? null : Regex.Replace(slug, @"\s+", " ");
        }

        private static bool TryGetPath(System.Text.Json.JsonElement element, out System.Text.Json.JsonElement found, params string[] path)
        {
            found = element;
            foreach (var key in path)
            {
                if (found.ValueKind != System.Text.Json.JsonValueKind.Object || !found.TryGetProperty(key, out found))
                    return false;
            }
            return found.ValueKind != System.Text.Json.JsonValueKind.Null;
        }

        private static string? GetString(System.Text.Json.JsonElement element, string name) =>
            element.ValueKind == System.Text.Json.JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;

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
                var image = ExtractBestImage(html, url);
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

        private static readonly Regex MetaTagPattern = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AttributePattern = new(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled);

        private static string? ExtractMetaContent(string html, string attrName, string attrValue)
        {
            // <meta property="og:image" content="..."> con atributos en cualquier orden y cualquier tipo de comillas.
            foreach (Match tag in MetaTagPattern.Matches(html))
            {
                string? key = null, content = null;
                foreach (Match attr in AttributePattern.Matches(tag.Value))
                {
                    var name = attr.Groups[1].Value;
                    var value = attr.Groups[2].Success ? attr.Groups[2].Value : attr.Groups[3].Value;
                    if (name.Equals(attrName, StringComparison.OrdinalIgnoreCase))
                        key = value;
                    else if (name.Equals("content", StringComparison.OrdinalIgnoreCase))
                        content = value;
                }

                if (key != null && key.Equals(attrValue, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(content))
                    return HtmlDecode(content);
            }

            return null;
        }

        /// <summary>
        /// Busca la mejor imagen del producto: metadatos sociales, JSON-LD y los formatos propios
        /// de Amazon y AliExpress. Devuelve una URL absoluta https o null.
        /// </summary>
        private static string? ExtractBestImage(string html, string pageUrl)
        {
            var candidates = new List<string?>
            {
                // Amazon: imagen grande del producto
                Regex.Match(html, @"data-old-hires=""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value,
                Regex.Match(html, @"""hiRes""\s*:\s*""([^""]+)""").Groups[1].Value,
                Regex.Match(html, @"data-a-dynamic-image=""\{&quot;([^&]+)&quot;", RegexOptions.IgnoreCase).Groups[1].Value,
                // AliExpress
                Regex.Match(html, @"""imagePathList""\s*:\s*\[\s*""([^""]+)""").Groups[1].Value,
                Regex.Match(html, @"""imageBigViewURL""\s*:\s*\[\s*""([^""]+)""").Groups[1].Value,
                // Metadatos estándar
                ExtractMetaContent(html, "property", "og:image:secure_url"),
                ExtractMetaContent(html, "property", "og:image"),
                ExtractMetaContent(html, "name", "og:image"),
                ExtractMetaContent(html, "name", "twitter:image"),
                ExtractMetaContent(html, "property", "twitter:image"),
                ExtractMetaContent(html, "itemprop", "image"),
                Regex.Match(html, @"<link\b[^>]*rel=[""']image_src[""'][^>]*href=[""']([^""']+)", RegexOptions.IgnoreCase).Groups[1].Value,
                ExtractJsonLdImage(html),
                Regex.Match(html, @"<img\b[^>]*\bid=""landingImage""[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value
            };

            foreach (var candidate in candidates)
            {
                var normalized = NormalizeImageUrl(candidate, pageUrl);
                if (normalized != null)
                    return normalized;
            }

            return null;
        }

        private static string? ExtractJsonLdImage(string html)
        {
            var scripts = Regex.Matches(html, @"<script\b[^>]*type=[""']application/ld\+json[""'][^>]*>(.*?)</script>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match script in scripts)
            {
                var m = Regex.Match(script.Groups[1].Value, @"""image""\s*:\s*(?:\[\s*)?(?:\{[^}]*""url""\s*:\s*)?""([^""]+)""",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (m.Success)
                    return m.Groups[1].Value;
            }
            return null;
        }

        /// <summary>Convierte a URL absoluta https y descarta datos inválidos (data:, svg de relleno, píxeles).</summary>
        public static string? NormalizeImageUrl(string? raw, string? pageUrl)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var value = HtmlDecode(raw.Trim())
                .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                .Replace("\\/", "/");

            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("sprite", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("transparent-pixel", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return null;

            if (value.StartsWith("//", StringComparison.Ordinal))
                value = "https:" + value;

            // En Linux "/img/a.jpg" se interpreta como file:///img/a.jpg: solo http(s) cuenta como absoluta.
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                if (string.IsNullOrWhiteSpace(pageUrl) ||
                    !Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) ||
                    !Uri.TryCreate(baseUri, value, out uri))
                    return null;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return null;

            // Mercado Libre y el navegador necesitan https.
            var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
            var result = builder.Uri.ToString();
            return result.Length <= 1000 ? result : null;
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
