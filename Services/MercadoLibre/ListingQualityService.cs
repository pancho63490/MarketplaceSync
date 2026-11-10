using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Algo que Mercado Libre pide para subir la calidad de la publicación.</summary>
public sealed class QualityAction
{
    [JsonPropertyName("t")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("l")] public string? Label { get; set; }
    [JsonPropertyName("u")] public string? Link { get; set; }
    /// <summary>true = baja la calificación hasta corregirlo (WARNING); false = oportunidad.</summary>
    [JsonPropertyName("w")] public bool Warning { get; set; }
}

/// <summary>
/// Calidad de cada publicación según Mercado Libre (<c>/item/{id}/performance</c>): calificación 0–100,
/// nivel (Básica, Estándar, Profesional) y lo que falta. Se revisa cada 3 días por publicación.
/// </summary>
public sealed class ListingQualityService
{
    private const int MaxActions = 8;
    private static readonly TimeSpan Recheck = TimeSpan.FromDays(3);
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListingQualityService> _logger;

    public ListingQualityService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<ListingQualityService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static string LevelLabel(string? level) => level switch
    {
        "Professional" => "Profesional",
        "Standard" => "Estándar",
        "Basic" => "Básica",
        _ => "Sin calcular"
    };

    public static string LevelTone(string? level) => level switch
    {
        "Professional" => "pill-ok",
        "Standard" => "pill-info",
        "Basic" => "pill-warn",
        _ => ""
    };

    public static List<QualityAction> Actions(MarketplacePublication publication)
    {
        if (string.IsNullOrWhiteSpace(publication.QualityActionsJson)) return new();
        try { return JsonSerializer.Deserialize<List<QualityAction>>(publication.QualityActionsJson) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Revisa hasta <paramref name="limit"/> publicaciones activas (primero las nunca revisadas y las que más venden).</summary>
    public async Task<int> RefreshAsync(Guid org, int limit = 40, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - Recheck;
        var publications = await _context.MarketplacePublications
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.IsPublished &&
                        p.Status == "active" && p.ExternalItemId != null && p.MarketplaceAccountId != null &&
                        (p.QualityCheckedAt == null || p.QualityCheckedAt < cutoff))
            .OrderBy(p => p.QualityCheckedAt != null).ThenByDescending(p => p.SoldQuantity ?? 0)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var tokens = new Dictionary<int, string?>();
        var client = _httpClientFactory.CreateClient();
        var updated = 0;
        foreach (var publication in publications)
        {
            var accountId = publication.MarketplaceAccountId!.Value;
            if (!tokens.TryGetValue(accountId, out var token))
            {
                try { token = await _auth.GetValidAccessTokenAsync(org, accountId); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(exception, "No token for quality check of account {Account}.", accountId);
                    token = null;
                }
                tokens[accountId] = token;
            }
            if (token is null) continue;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/item/{Uri.EscapeDataString(publication.ExternalItemId!)}/performance");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Mercado Libre todavía no calcula la calidad de esta publicación.
                    publication.QualityCheckedAt = DateTime.UtcNow;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("performance for {Item} returned {Status}.", publication.ExternalItemId, (int)response.StatusCode);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        break;
                    continue;
                }
                Apply(publication, content);
                updated++;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "performance failed for {Item}.", publication.ExternalItemId);
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        return updated;
    }

    private static void Apply(MarketplacePublication publication, string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        publication.QualityCheckedAt = DateTime.UtcNow;
        publication.QualityScore = Int(root, "score") is { } score ? Math.Clamp(score, 0, 100) : null;
        publication.QualityLevel = Str(root, "level") is { Length: <= 20 } level ? level : null;

        var actions = new List<QualityAction>();
        if (root.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("variables", out var variables) || variables.ValueKind != JsonValueKind.Array) continue;
                foreach (var variable in variables.EnumerateArray())
                {
                    if (!variable.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) continue;
                    foreach (var rule in rules.EnumerateArray())
                    {
                        if (Str(rule, "status") != "PENDING") continue;
                        var wordings = rule.TryGetProperty("wordings", out var w) && w.ValueKind == JsonValueKind.Object ? w : default;
                        var title = Str(wordings, "title") ?? Str(variable, "title");
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        actions.Add(new QualityAction
                        {
                            Title = Cut(title.Trim(), 200),
                            Label = Str(wordings, "label") is { Length: > 0 } label ? Cut(label.Trim(), 60) : null,
                            Link = SafeLink(Str(wordings, "link")),
                            Warning = Str(rule, "mode") == "WARNING"
                        });
                    }
                }
            }
        }

        var top = actions.OrderByDescending(a => a.Warning).DistinctBy(a => a.Title).Take(MaxActions).ToList();
        var json = top.Count == 0 ? null : JsonSerializer.Serialize(top, Json);
        while (json is { Length: > 4000 } && top.Count > 1)
        {
            top.RemoveAt(top.Count - 1);
            json = JsonSerializer.Serialize(top, Json);
        }
        publication.QualityActionsJson = json is { Length: > 4000 } ? null : json;
    }

    /// <summary>Solo links https de Mercado Libre (los manda la API para corregir en su página).</summary>
    private static string? SafeLink(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.Host.ToLowerInvariant();
        var ok = host.EndsWith(".mercadolibre.com.mx") || host == "mercadolibre.com.mx" || host.EndsWith(".mercadolibre.com") || host == "mercadolibre.com";
        return ok && link!.Length <= 500 ? link : null;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? (int)Math.Round(d) : null;
}
