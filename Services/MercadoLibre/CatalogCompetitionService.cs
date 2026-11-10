using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Una publicación de catálogo que cambió de estado (por ejemplo, empezó a perder).</summary>
public sealed record CatalogChange(MarketplacePublication Publication, string? OldStatus, string NewStatus);

/// <summary>Fila de la pantalla: estado en el catálogo y lo que ganarías al precio para ganar.</summary>
public sealed record CatalogRow(MarketplacePublication Publication, PublicationMoney? Now, PublicationMoney? AtPriceToWin)
{
    /// <summary>Bajar al precio para ganar deja ganancia (aunque sea menos que tu mínimo).</summary>
    public bool CanMatch => Publication.CatalogStatus == CatalogCompetitionService.Losing &&
                            Publication.AutoPriceStatus != "ACTIVE" &&
                            Publication.CatalogPriceToWin is > 0 && Publication.Price is > 0 &&
                            Publication.CatalogPriceToWin < Publication.Price &&
                            AtPriceToWin is { Profit.NetProfitMxn: > 0 };
}

/// <summary>
/// Competencia en catálogo: para cada publicación de catálogo pregunta a Mercado Libre
/// (<c>/items/{id}/price_to_win?version=v2</c>) si gana el botón de compra, a qué precio ganaría y por qué no compite.
/// No cambia precios: eso se hace con botón de confirmar.
/// </summary>
public sealed class CatalogCompetitionService
{
    public const string Winning = "winning";
    public const string Sharing = "sharing_first_place";
    public const string Losing = "competing";
    public const string Listed = "listed";

    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(90);

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly PublicationEconomics _economics;
    private readonly ICrossBorderPricingService _pricing;
    private readonly ILogger<CatalogCompetitionService> _logger;

    public CatalogCompetitionService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, PublicationEconomics economics, ICrossBorderPricingService pricing, ILogger<CatalogCompetitionService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _economics = economics;
        _pricing = pricing;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static string StatusLabel(string? status) => status switch
    {
        Winning => "Ganando",
        Sharing => "Compartiendo el primer lugar",
        Losing => "Perdiendo",
        Listed => "No compite",
        _ => "Sin revisar"
    };

    public static string StatusTone(string? status) => status switch
    {
        Winning => "pill-ok",
        Sharing => "pill-info",
        Losing => "pill-bad",
        Listed => "pill-warn",
        _ => ""
    };

    /// <summary>Por qué no compite, en palabras del vendedor.</summary>
    public static string ReasonLabel(string reason) => reason switch
    {
        "non_trusted_seller" => "Mercado Libre todavía no deja competir a la cuenta",
        "reputation_below_threshold" => "Tu reputación está abajo de lo que pide el catálogo",
        "item_reputation_below_threshold" => "La publicación tiene mala calificación",
        "winner_has_better_reputation" => "El ganador tiene mejor reputación",
        "manufacturing_time" or "temporarily_winning_manufacturing_time" or "temporarily_competing_manufacturing_time" => "Tiene tiempo de disponibilidad (días para enviar)",
        "temporarily_winning_best_reputation_available" or "temporarily_competing_best_reputation_available" => "Mercado Libre muestra por ahora a quien tiene mejor reputación",
        "item_paused" => "La publicación está pausada",
        "item_not_opted_in" => "La publicación no está en la ficha de catálogo",
        "shipping_mode" => "Su tipo de envío no compite",
        "newbie_program_seller" => "La cuenta está en el programa de vendedores nuevos",
        _ => reason.Replace('_', ' ')
    };

    /// <summary>Ventajas que el ganador tiene y tú podrías activar.</summary>
    public static string BoostLabel(string boost) => boost switch
    {
        "same_day_shipping" => "envío el mismo día",
        "fulfillment" => "Full",
        "free_installments" => "meses sin intereses",
        "free_shipping" => "envío gratis",
        "shipping_collect" => "recoger en punto",
        _ => boost.Replace('_', ' ')
    };

    /// <summary>Revisa las publicaciones de catálogo activas que no se han revisado en 90 minutos. Regresa las que cambiaron de estado.</summary>
    public async Task<List<CatalogChange>> RefreshAsync(Guid org, int limit = 60, bool force = false, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - Recheck;
        var publications = await _context.MarketplacePublications.Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.IsPublished &&
                        p.CatalogListing && p.Status == "active" && p.ExternalItemId != null && p.MarketplaceAccountId != null &&
                        (force || p.CatalogCheckedAt == null || p.CatalogCheckedAt < cutoff))
            .OrderBy(p => p.CatalogCheckedAt ?? DateTime.MinValue)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var changes = new List<CatalogChange>();
        var tokens = new Dictionary<int, string?>();
        var client = _httpClientFactory.CreateClient();
        foreach (var publication in publications)
        {
            var accountId = publication.MarketplaceAccountId!.Value;
            if (!tokens.TryGetValue(accountId, out var token))
            {
                try { token = await _auth.GetValidAccessTokenAsync(org, accountId); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(exception, "No token for catalog check of account {Account}.", accountId);
                    token = null;
                }
                tokens[accountId] = token;
            }
            if (token is null) continue;

            var old = publication.CatalogStatus;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiBase}/items/{Uri.EscapeDataString(publication.ExternalItemId!)}/price_to_win?version=v2");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                publication.CatalogCheckedAt = DateTime.UtcNow;
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("price_to_win for {Item} returned {Status}.", publication.ExternalItemId, (int)response.StatusCode);
                    continue;
                }
                Apply(publication, content);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "price_to_win failed for {Item}.", publication.ExternalItemId);
                continue;
            }

            if (publication.CatalogStatus is { } now && now != old)
                changes.Add(new CatalogChange(publication, old, now));
        }
        await _context.SaveChangesAsync(cancellationToken);
        return changes;
    }

    private static void Apply(MarketplacePublication publication, string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        publication.CatalogStatus = Str(root, "status") is { Length: > 0 and <= 30 } status ? status : publication.CatalogStatus;
        publication.CatalogPriceToWin = Dec(root, "price_to_win");
        publication.CatalogWinnerPrice = root.TryGetProperty("winner", out var winner) && winner.ValueKind == JsonValueKind.Object ? Dec(winner, "price") : null;
        publication.CatalogProductId ??= Str(root, "catalog_product_id") is { Length: <= 40 } product ? product : null;

        var reasons = root.TryGetProperty("reason", out var reasonList) && reasonList.ValueKind == JsonValueKind.Array
            ? reasonList.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToList()
            : new List<string>();
        publication.CatalogReasons = reasons.Count == 0 ? null : Cut(string.Join(',', reasons), 400);

        // Ventajas que Mercado Libre marca como oportunidad (las tiene el ganador o te ayudarían a ganar).
        var opportunities = root.TryGetProperty("boosts", out var boosts) && boosts.ValueKind == JsonValueKind.Array
            ? boosts.EnumerateArray().Where(b => Str(b, "status") == "opportunity").Select(b => Str(b, "id")).Where(id => id is not null).Cast<string>().ToList()
            : new List<string>();
        publication.CatalogOpportunities = opportunities.Count == 0 ? null : Cut(string.Join(',', opportunities), 300);
    }

    /// <summary>Publicaciones de catálogo con su ganancia ahora y al precio para ganar.</summary>
    public async Task<List<CatalogRow>> RowsAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking().Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.IsPublished && p.CatalogListing)
            .ToListAsync(cancellationToken);
        return publications
            .Select(p => new CatalogRow(p, _economics.Calculate(p, p.Product, settings),
                p.CatalogPriceToWin is > 0 ? _economics.Calculate(p, p.Product, settings, p.CatalogPriceToWin) : null))
            .OrderBy(r => r.Publication.CatalogStatus switch { Losing => 0, Listed => 1, Sharing => 2, Winning => 3, _ => 4 })
            .ThenByDescending(r => r.Publication.SoldQuantity ?? 0)
            .ToList();
    }

    public static IEnumerable<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? Enumerable.Empty<string>() : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
