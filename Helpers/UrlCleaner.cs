using System.Text.RegularExpressions;

namespace MarketplaceSync.Web.Helpers;

/// <summary>
/// Deja los links de tiendas en su forma corta (sin parámetros de rastreo) y dentro del largo que cabe en la base.
/// Un link de Amazon, Walmart o eBay copiado del celular puede pasar de 1,000 caracteres por los parámetros
/// de búsqueda y rastreo; el producto se identifica igual con la forma corta.
/// </summary>
public static class UrlCleaner
{
    public const int MaxLength = 1000;

    private static readonly Regex Ebay = new(@"^https?://(?:www\.|m\.)?ebay\.[a-z.]+/itm/(?:[^/?#]+/)?(\d{9,15})", RegexOptions.IgnoreCase);
    private static readonly Regex Amazon = new(@"^https?://(?:www\.|smile\.|m\.)?(amazon\.[a-z.]+)/(?:([^/?#]+)/)?(?:dp|gp/product|gp/aw/d)/([A-Z0-9]{10})", RegexOptions.IgnoreCase);
    private static readonly Regex Walmart = new(@"^https?://(?:www\.)?walmart\.com/ip/(?:([^/?#]+)/)?(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex AliExpress = new(@"^https?://(?:[a-z]+\.)?aliexpress\.[a-z.]+/item/(\d+)\.html", RegexOptions.IgnoreCase);

    private static readonly string[] TrackingPrefixes =
    {
        "utm_", "fbclid", "gclid", "msclkid", "_trkparms", "_trksid", "hash", "mkcid", "mkrid", "campid", "customid",
        "toolid", "mkevt", "pd_rd_", "pf_rd_", "ref_", "ref", "psc", "th", "content-id", "qid", "sr", "crid", "sprefix",
        "keywords", "dib", "dib_tag", "spm", "scm", "algo_", "pdp_", "aff_", "sk", "athena", "from", "classtype",
        "wmlspartner", "selectedsellerid", "adid", "wl", "veh", "sourceid", "affiliates_ad_id", "irgwc", "clickid"
    };

    /// <summary>Forma corta del link de la tienda; si no se reconoce, solo quita parámetros de rastreo.</summary>
    public static string CleanSourceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        url = url.Trim();

        var ebay = Ebay.Match(url);
        if (ebay.Success)
            return $"https://www.ebay.com/itm/{ebay.Groups[1].Value}";

        var amazon = Amazon.Match(url);
        if (amazon.Success)
        {
            // Se conserva el nombre del link: sirve de título cuando Amazon no deja leer la página.
            var slug = amazon.Groups[2].Success && amazon.Groups[2].Value.Length < 300 ? amazon.Groups[2].Value + "/" : string.Empty;
            return $"https://www.{amazon.Groups[1].Value.ToLowerInvariant()}/{slug}dp/{amazon.Groups[3].Value.ToUpperInvariant()}";
        }

        var walmart = Walmart.Match(url);
        if (walmart.Success)
        {
            var slug = walmart.Groups[1].Success && walmart.Groups[1].Value.Length < 300 ? walmart.Groups[1].Value + "/" : string.Empty;
            return $"https://www.walmart.com/ip/{slug}{walmart.Groups[2].Value}";
        }

        var ali = AliExpress.Match(url);
        if (ali.Success)
            return $"https://www.aliexpress.com/item/{ali.Groups[1].Value}.html";

        return Fit(StripTracking(url)) ?? string.Empty;
    }

    /// <summary>Para fotos y otros links: si no cabe, se intenta sin parámetros; si aun así no cabe, null.</summary>
    public static string? Fit(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length <= MaxLength)
            return url;
        var withoutQuery = url.Split('?', '#')[0];
        return withoutQuery.Length <= MaxLength ? withoutQuery : null;
    }

    private static string StripTracking(string url)
    {
        var hashIndex = url.IndexOf('#');
        if (hashIndex >= 0)
            url = url[..hashIndex];
        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0)
            return url;

        var kept = url[(queryIndex + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair =>
            {
                var key = pair.Split('=')[0].ToLowerInvariant();
                return !TrackingPrefixes.Any(prefix => key == prefix || key.StartsWith(prefix, StringComparison.Ordinal));
            })
            .ToList();
        return kept.Count == 0 ? url[..queryIndex] : url[..queryIndex] + "?" + string.Join('&', kept);
    }
}
