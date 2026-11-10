using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed class ReviewsSummary
{
    public decimal? Average { get; init; }
    public int Count { get; init; }
    public int LowLast30 { get; init; }
    /// <summary>Publicaciones con peor promedio (al menos una opinión).</summary>
    public List<MarketplacePublication> Worst { get; init; } = new();
    /// <summary>Opiniones de 1 y 2 estrellas recientes.</summary>
    public List<ItemReview> RecentLow { get; init; } = new();
}

/// <summary>
/// Opiniones de compradores (<c>/reviews/item/{id}</c>): promedio y estrellas por publicación, y las opiniones nuevas.
/// Las de 1 y 2 estrellas se avisan por Telegram para corregir rápido la publicación.
/// </summary>
public sealed class ReviewService
{
    public const int LowRate = 2;
    private static readonly TimeSpan Recheck = TimeSpan.FromHours(12);

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<ReviewService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static string Stars(int rate) => new string('★', Math.Clamp(rate, 0, 5)) + new string('☆', 5 - Math.Clamp(rate, 0, 5));

    /// <summary>Revisa las publicaciones que ya vendieron (cada 12 h por publicación). Regresa las opiniones de 1–2 estrellas nuevas (de los últimos 14 días).</summary>
    public async Task<List<ItemReview>> RefreshAsync(Guid org, int limit = 80, bool force = false, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - Recheck;
        var publications = await _context.MarketplacePublications.Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.IsPublished &&
                        p.ExternalItemId != null && p.MarketplaceAccountId != null && (p.SoldQuantity ?? 0) > 0 &&
                        (force || p.ReviewsCheckedAt == null || p.ReviewsCheckedAt < cutoff))
            .OrderBy(p => p.ReviewsCheckedAt != null).ThenBy(p => p.ReviewsCheckedAt).ThenByDescending(p => p.SoldQuantity)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (publications.Count == 0) return new();

        var pubIds = publications.Select(p => p.Id).ToList();
        var known = (await _context.ItemReviews.AsNoTracking()
            .Where(r => r.OrganizationId == org && pubIds.Contains(r.PublicationId))
            .Select(r => r.ExternalId).ToListAsync(cancellationToken)).ToHashSet();

        var tokens = new Dictionary<int, string?>();
        var client = _httpClientFactory.CreateClient();
        var newLow = new List<ItemReview>();
        var recent = DateTime.UtcNow.AddDays(-14);
        foreach (var publication in publications)
        {
            var accountId = publication.MarketplaceAccountId!.Value;
            if (!tokens.TryGetValue(accountId, out var token))
            {
                try { token = await _auth.GetValidAccessTokenAsync(org, accountId); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(exception, "No token for reviews of account {Account}.", accountId);
                    token = null;
                }
                tokens[accountId] = token;
            }
            if (token is null) continue;

            var url = $"{ApiBase}/reviews/item/{Uri.EscapeDataString(publication.ExternalItemId!)}?limit=20";
            if (publication.CatalogListing && publication.CatalogProductId is { } catalog)
                url += "&catalog_product_id=" + Uri.EscapeDataString(catalog);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                publication.ReviewsCheckedAt = DateTime.UtcNow;
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("reviews for {Item} returned {Status}.", publication.ExternalItemId, (int)response.StatusCode);
                    continue;
                }

                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                publication.RatingAverage = Dec(root, "rating_average") is { } avg and > 0 ? Math.Round(avg, 2) : publication.RatingAverage;
                if (root.TryGetProperty("rating_levels", out var levels) && levels.ValueKind == JsonValueKind.Object)
                    publication.RatingCount = levels.EnumerateObject().Sum(l => l.Value.TryGetInt32(out var n) ? n : 0);

                if (!root.TryGetProperty("reviews", out var reviews) || reviews.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in reviews.EnumerateArray())
                {
                    var id = Str(r, "id");
                    if (id is null || id.Length > 40 || known.Contains(id)) continue;
                    if (Str(r, "status") is { } status && status != "published") continue;
                    var rate = r.TryGetProperty("rate", out var rv) && rv.TryGetInt32(out var rr) ? Math.Clamp(rr, 1, 5) : 0;
                    if (rate == 0) continue;
                    var review = new ItemReview
                    {
                        OrganizationId = org,
                        PublicationId = publication.Id,
                        ExternalId = id,
                        Rate = rate,
                        Title = Cut(Str(r, "title"), 200),
                        Content = Cut(Str(r, "content"), 2000),
                        CreatedAt = Date(Str(r, "date_created")) ?? DateTime.UtcNow,
                        BuyingDate = Date(Str(r, "buying_date"))
                    };
                    review.Publication = publication;
                    _context.ItemReviews.Add(review);
                    known.Add(id);
                    if (rate <= LowRate && review.CreatedAt >= recent)
                        newLow.Add(review);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "reviews failed for {Item}.", publication.ExternalItemId);
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        return newLow;
    }

    public async Task<ReviewsSummary> SummaryAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var rated = _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.IsPublished && p.RatingCount > 0 && p.RatingAverage != null);
        var count = await rated.SumAsync(p => p.RatingCount ?? 0, cancellationToken);
        var weighted = count == 0 ? null : (decimal?)(await rated.SumAsync(p => p.RatingAverage!.Value * (p.RatingCount ?? 0), cancellationToken) / count);
        var since = DateTime.UtcNow.AddDays(-30);
        return new ReviewsSummary
        {
            Average = weighted is { } w ? Math.Round(w, 1) : null,
            Count = count,
            LowLast30 = await _context.ItemReviews.CountAsync(r => r.OrganizationId == org && r.Rate <= LowRate && r.CreatedAt >= since, cancellationToken),
            Worst = await rated.Include(p => p.Product).Where(p => p.RatingAverage < 4.5m)
                .OrderBy(p => p.RatingAverage).ThenByDescending(p => p.RatingCount).Take(15).ToListAsync(cancellationToken),
            RecentLow = await _context.ItemReviews.AsNoTracking().Include(r => r.Publication!).ThenInclude(p => p.Product)
                .Where(r => r.OrganizationId == org && r.Rate <= 3 && r.CreatedAt >= DateTime.UtcNow.AddDays(-90))
                .OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(cancellationToken)
        };
    }

    private static string? Cut(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTime? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
}
