using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.CrossBorder;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Una campaña o promoción de Mercado Libre (DEAL, SELLER_CAMPAIGN, SMART…).</summary>
public sealed record MlPromotion(
    string Id, string Type, string? SubType, string Status, string Name,
    DateTime? Start, DateTime? Finish, DateTime? Deadline, int AccountId)
{
    public string TypeLabel => PromotionService.TypeLabel(Type);
}

/// <summary>Una publicación dentro de una promoción (candidata o ya participando).</summary>
public sealed class PromotionOffer
{
    public string ItemId { get; set; } = string.Empty;
    public string? PromotionId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? SubType { get; set; }
    public string? Name { get; set; }

    /// <summary>candidate, pending, started, finished, sync_requested…</summary>
    public string Status { get; set; } = string.Empty;

    public decimal? Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public decimal? SuggestedPrice { get; set; }
    public decimal? MeliPercent { get; set; }
    public decimal? SellerPercent { get; set; }
    public string? OfferId { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }

    public bool IsActive => PromotionService.IsActiveStatus(Status);
    public bool IsCandidate => Status == "candidate";
}

/// <summary>Renglón de la pantalla: la oferta + la publicación de la app + cuánto se gana con ese precio.</summary>
public sealed class CampaignRow
{
    public required PromotionOffer Offer { get; init; }
    public int PublicationId { get; init; }
    public string Title { get; init; } = string.Empty;
    public decimal CurrentPrice { get; init; }

    /// <summary>Precio que se propone mandar (el sugerido o el de menor descuento permitido).</summary>
    public decimal? ProposedPrice { get; init; }

    /// <summary>El vendedor escoge el precio (DEAL, campaña propia flexible, relámpago…).</summary>
    public bool PriceEditable { get; init; }

    public bool CostKnown { get; init; }

    /// <summary>Ganancia por pieza con el precio de la promoción (incluye lo que pone Mercado Libre).</summary>
    public decimal? Profit { get; init; }
    public decimal? ProfitNow { get; init; }

    /// <summary>Sigue ganando al menos la ganancia mínima de Costos de cruce.</summary>
    public bool Earns { get; init; }

    public decimal? DiscountPercent => CurrentPrice > 0 && ProposedPrice is > 0
        ? Math.Round((1 - ProposedPrice.Value / CurrentPrice) * 100m, 0)
        : null;
}

public sealed class CampaignView
{
    public required MlPromotion Promotion { get; init; }
    public List<CampaignRow> Rows { get; init; } = new();
    public int Candidates => Rows.Count(r => r.Offer.IsCandidate);
    public int Joined => Rows.Count(r => r.Offer.IsActive);
    public int CandidatesThatEarn => Rows.Count(r => r.Offer.IsCandidate && r.Earns);
}

/// <summary>Precio y ganancia con un % de descuento (para escoger el descuento en Editar).</summary>
public sealed record DiscountOption(int Percent, decimal Price, decimal? Profit, bool Earns);

/// <summary>Lo que se guarda en la acción de Telegram para entrar a una campaña.</summary>
public sealed class PromotionJoinPayload
{
    public string PromotionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? SubType { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<PromotionJoinItem> Items { get; set; } = new();
}

public sealed class PromotionJoinItem
{
    public int PublicationId { get; set; }
    public decimal? Price { get; set; }
    public string? OfferId { get; set; }
}

/// <summary>
/// Promociones de Mercado Libre con la API oficial <c>/seller-promotions</c>: campañas a las que te invitan
/// (con la ganancia que deja cada publicación a ese precio), descuentos individuales (PRICE_DISCOUNT, 5–80%,
/// máximo 31 días) y salir de una promoción. Nada entra a una promoción sin que el vendedor lo pida.
/// </summary>
public sealed class PromotionService
{
    public const int MinDiscount = 5;
    public const int MaxDiscount = 80;
    public const int MaxDays = 31;
    private const int MaxPromotions = 20;
    private const int MaxItemPages = 6;
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");
    private static readonly TimeZoneInfo Juarez = FindJuarez();

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ICrossBorderPricingService _pricing;
    private readonly PublicationEconomics _economics;
    private readonly ILogger<PromotionService> _logger;

    public PromotionService(
        AppDbContext context,
        IMercadoLibreAuthService auth,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ICrossBorderPricingService pricing,
        PublicationEconomics economics,
        ILogger<PromotionService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _pricing = pricing;
        _economics = economics;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public static string TypeLabel(string? type) => type switch
    {
        "PRICE_DISCOUNT" => "Descuento tuyo",
        "DEAL" => "Campaña de Mercado Libre",
        "MARKETPLACE_CAMPAIGN" => "Campaña co-fondeada",
        "SELLER_CAMPAIGN" => "Tu campaña",
        "LIGHTNING" => "Oferta relámpago",
        "DOD" => "Oferta del día",
        "VOLUME" => "Descuento por volumen",
        "PRE_NEGOTIATED" => "Descuento negociado",
        "SMART" => "Campaña automatizada",
        "PRICE_MATCHING" => "Precio competitivo",
        "UNHEALTHY_STOCK" => "Liquidación de stock",
        _ => "Promoción"
    };

    public static bool IsActiveStatus(string? status) => status is "started" or "pending" or "sync_requested";

    /// <summary>En estas el vendedor pone el precio; en las demás lo calcula Mercado Libre.</summary>
    public static bool PriceIsChosen(string type, string? subType) =>
        type is "DEAL" or "LIGHTNING" or "DOD" || (type == "SELLER_CAMPAIGN" && subType != "FIXED_PERCENTAGE");

    // ─────────────────────────────────────────────────────────────
    // Campañas a las que te invitan
    // ─────────────────────────────────────────────────────────────

    public async Task<List<CampaignView>> CampaignsAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .ToListAsync(cancellationToken);
        var publications = await _context.MarketplacePublications
            .Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null && p.MarketplaceAccountId != null)
            .ToListAsync(cancellationToken);
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);

        var views = new List<CampaignView>();
        foreach (var account in accounts)
        {
            var token = await TokenAsync(org, account.Id);
            if (token is null)
                continue;

            var promotions = await SellerPromotionsAsync(token, account, cancellationToken);
            var byItem = publications.Where(p => p.MarketplaceAccountId == account.Id)
                .GroupBy(p => p.ExternalItemId!).ToDictionary(g => g.Key, g => g.First());

            foreach (var promotion in promotions.Where(p => p.Status != "finished" && p.Type != "PRICE_DISCOUNT").Take(MaxPromotions))
            {
                var offers = await PromotionItemsAsync(token, promotion, cancellationToken);
                var rows = new List<CampaignRow>();
                foreach (var offer in offers.Where(o => o.Status is "candidate" || o.IsActive))
                {
                    if (!byItem.TryGetValue(offer.ItemId, out var publication))
                        continue;
                    rows.Add(BuildRow(publication, offer, settings));
                    if (offer.IsActive)
                        ApplyActive(publication, offer);
                }
                if (rows.Count > 0)
                    views.Add(new CampaignView
                    {
                        Promotion = promotion,
                        Rows = rows.OrderByDescending(r => r.Offer.IsActive).ThenByDescending(r => r.Earns).ThenByDescending(r => r.Profit ?? decimal.MinValue).ToList()
                    });
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        return views.OrderBy(v => v.Promotion.Deadline ?? v.Promotion.Finish ?? DateTime.MaxValue).ToList();
    }

    public CampaignRow BuildRow(MarketplacePublication publication, PromotionOffer offer, CrossBorderSettings settings)
    {
        var current = offer.OriginalPrice is > 0 ? offer.OriginalPrice.Value : publication.Price ?? 0;
        var editable = PriceIsChosen(offer.Type, offer.SubType);
        var proposed = offer.IsActive ? offer.Price : DefaultPrice(offer, current, editable);
        var buyerPays = proposed;
        var sellerReceives = SellerPrice(proposed, current, offer.MeliPercent, offer.SellerPercent);
        var money = sellerReceives is > 0 ? _economics.Calculate(publication, publication.Product, settings, sellerReceives) : null;
        var now = _economics.Calculate(publication, publication.Product, settings, current > 0 ? current : null);
        return new CampaignRow
        {
            Offer = offer,
            PublicationId = publication.Id,
            Title = publication.Product.Title,
            CurrentPrice = current,
            ProposedPrice = buyerPays,
            PriceEditable = editable && !offer.IsActive,
            CostKnown = money is not null,
            Profit = money?.Profit.NetProfitMxn,
            ProfitNow = now?.Profit.NetProfitMxn,
            Earns = money is not null && money.Profit.NetProfitMxn >= settings.MinProfitAmountMxn
        };
    }

    /// <summary>
    /// Lo que de verdad recibe el vendedor: en co-fondeadas Mercado Libre pone su parte del descuento,
    /// así que el vendedor cobra el precio original menos solo su porcentaje.
    /// </summary>
    public static decimal? SellerPrice(decimal? buyerPrice, decimal original, decimal? meliPercent, decimal? sellerPercent)
    {
        if (meliPercent is > 0 && sellerPercent is >= 0 && original > 0)
            return Math.Round(original * (1 - sellerPercent.Value / 100m), 2);
        return buyerPrice;
    }

    private static decimal? DefaultPrice(PromotionOffer offer, decimal current, bool editable)
    {
        if (offer.SuggestedPrice is > 0)
            return offer.SuggestedPrice;
        if (!editable)
            return offer.Price is > 0 ? offer.Price : null;
        // El menor descuento que acepta la campaña: más ganancia.
        if (offer.MaxPrice is > 0)
            return Math.Floor(offer.MaxPrice.Value);
        if (current <= 0)
            return null;
        var price = Math.Floor(current * 0.95m);
        return offer.MinPrice is > 0 && price < offer.MinPrice ? Math.Ceiling(offer.MinPrice.Value) : price;
    }

    // ─────────────────────────────────────────────────────────────
    // Una publicación
    // ─────────────────────────────────────────────────────────────

    /// <summary>Promociones de una publicación (en las que está y a las que puede entrar).</summary>
    public async Task<List<PromotionOffer>?> ItemPromotionsAsync(Guid org, MarketplacePublication publication, CancellationToken cancellationToken = default)
    {
        if (publication.ExternalItemId is null || publication.MarketplaceAccountId is null)
            return null;
        var token = await TokenAsync(org, publication.MarketplaceAccountId.Value);
        if (token is null)
            return null;
        var (ok, content) = await SendAsync(token, HttpMethod.Get,
            $"/seller-promotions/items/{Uri.EscapeDataString(publication.ExternalItemId)}?app_version=v2", null, cancellationToken);
        if (!ok)
            return content.Contains("not_found", StringComparison.OrdinalIgnoreCase) ? new List<PromotionOffer>() : null;
        return ParseOffers(content, publication.ExternalItemId, defaultPromotion: null);
    }

    /// <summary>Vuelve a leer en qué promoción está la publicación y lo guarda.</summary>
    public async Task<List<PromotionOffer>?> RefreshAsync(Guid org, int publicationId, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication is null)
            return null;
        var offers = await ItemPromotionsAsync(org, publication, cancellationToken);
        if (offers is null)
            return null;
        var active = offers.Where(o => o.IsActive).OrderBy(o => o.Status == "started" ? 0 : 1).FirstOrDefault();
        if (active is null)
            ClearActive(publication);
        else
            ApplyActive(publication, active);
        publication.PromotionCheckedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return offers;
    }

    /// <summary>Revisa las publicaciones activas (todos los días): qué promociones terminaron o empezaron.</summary>
    public async Task<int> RefreshAllAsync(Guid org, int limit = 150, CancellationToken cancellationToken = default)
    {
        var ids = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null &&
                        p.MarketplaceAccountId != null && (p.Status == "active" || p.PromotionType != null))
            .OrderBy(p => p.PromotionCheckedAt ?? DateTime.MinValue)
            .Select(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var refreshed = 0;
        foreach (var id in ids)
        {
            if (await RefreshAsync(org, id, cancellationToken) is not null)
                refreshed++;
        }
        return refreshed;
    }

    public async Task<List<DiscountOption>> DiscountOptionsAsync(Guid org, MarketplacePublication publication, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        return DiscountOptions(publication, settings);
    }

    public List<DiscountOption> DiscountOptions(MarketplacePublication publication, CrossBorderSettings settings)
    {
        var basePrice = BasePrice(publication);
        var options = new List<DiscountOption>();
        if (basePrice is not > 0)
            return options;
        foreach (var percent in new[] { 5, 10, 15, 20, 25, 30, 35, 40, 50 })
        {
            var price = DiscountedPrice(basePrice.Value, percent);
            var money = _economics.Calculate(publication, publication.Product, settings, price);
            options.Add(new DiscountOption(percent, price, money?.Profit.NetProfitMxn,
                money is not null && money.Profit.NetProfitMxn >= settings.MinProfitAmountMxn));
        }
        return options;
    }

    /// <summary>Precio sin promoción (si ya tiene una, el original).</summary>
    public static decimal? BasePrice(MarketplacePublication publication) =>
        publication.PromotionType is not null && publication.PromotionOriginalPrice is > 0 ? publication.PromotionOriginalPrice : publication.Price;

    public static decimal DiscountedPrice(decimal basePrice, int percent) =>
        Math.Round(basePrice * (100 - percent) / 100m, 2, MidpointRounding.ToZero);

    // ─────────────────────────────────────────────────────────────
    // Acciones
    // ─────────────────────────────────────────────────────────────

    /// <summary>Descuento individual (PRICE_DISCOUNT) desde ahora y por <paramref name="days"/> días.</summary>
    public async Task<(bool Ok, string Message)> CreateDiscountAsync(Guid org, int publicationId, int percent, int days, CancellationToken cancellationToken = default)
    {
        if (percent < MinDiscount || percent > MaxDiscount)
            return (false, $"El descuento debe ser de {MinDiscount}% a {MaxDiscount}%.");
        days = Math.Clamp(days, 1, MaxDays);
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication?.ExternalItemId is null || publication.MarketplaceAccountId is null)
            return (false, "La publicación no está en Mercado Libre.");
        if (publication.Condition is { Length: > 0 } condition && !condition.Equals("new", StringComparison.OrdinalIgnoreCase) &&
            !condition.StartsWith("nuev", StringComparison.OrdinalIgnoreCase))
            return (false, "Mercado Libre solo deja poner descuento a productos nuevos.");
        if (publication.ListingTypeId == "free")
            return (false, "Las publicaciones gratuitas no pueden tener descuento.");

        var token = await TokenAsync(org, publication.MarketplaceAccountId.Value);
        if (token is null)
            return (false, "No hay sesión de Mercado Libre para esa cuenta.");

        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Juarez);
        var finish = start.Date.AddDays(days).AddHours(23).AddMinutes(59);
        var body = new JsonObject
        {
            ["promotion_type"] = "PRICE_DISCOUNT",
            ["discount_percent"] = percent,
            ["start_date"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["finish_date"] = finish.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
        };
        var (ok, content) = await SendAsync(token, HttpMethod.Post,
            $"/seller-promotions/items/{Uri.EscapeDataString(publication.ExternalItemId)}?app_version=v2", body, cancellationToken);
        if (!ok)
            return (false, "Mercado Libre no aceptó el descuento: " + DescribeError(content));

        var (price, original) = ReadPrices(content);
        publication.PromotionType = "PRICE_DISCOUNT";
        publication.PromotionId = null;
        publication.PromotionOfferId = null;
        publication.PromotionStatus = "started";
        publication.PromotionOriginalPrice = original ?? BasePrice(publication);
        publication.PromotionPrice = price ?? (publication.PromotionOriginalPrice is > 0 ? DiscountedPrice(publication.PromotionOriginalPrice.Value, percent) : null);
        publication.PromotionMeliPercent = null;
        publication.PromotionEndsAt = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(finish, DateTimeKind.Unspecified), Juarez);
        publication.PromotionCheckedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return (true, $"Listo: {percent}% de descuento a «{Short(publication.Product.Title, 50)}» hasta el {finish:dd/MM}" +
                      (publication.PromotionPrice is > 0 ? $" ({Money(publication.PromotionOriginalPrice)} → {Money(publication.PromotionPrice)})." : "."));
    }

    /// <summary>Mete la publicación a una campaña de Mercado Libre.</summary>
    public async Task<(bool Ok, string Message)> JoinAsync(Guid org, int publicationId, string promotionId, string type, string? subType, decimal? dealPrice, string? offerId, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication?.ExternalItemId is null || publication.MarketplaceAccountId is null)
            return (false, "La publicación no está en Mercado Libre.");
        if (publication.PromotionId == promotionId && IsActiveStatus(publication.PromotionStatus))
            return (true, $"«{Short(publication.Product.Title, 40)}» ya estaba en la campaña.");
        var token = await TokenAsync(org, publication.MarketplaceAccountId.Value);
        if (token is null)
            return (false, "No hay sesión de Mercado Libre para esa cuenta.");

        var body = new JsonObject { ["promotion_id"] = promotionId, ["promotion_type"] = type };
        if (PriceIsChosen(type, subType))
        {
            if (dealPrice is not > 0)
                return (false, "Falta el precio con el que entra a la campaña.");
            body["deal_price"] = Math.Round(dealPrice.Value, 2);
        }
        if (!string.IsNullOrWhiteSpace(offerId))
            body["offer_id"] = offerId;

        var (ok, content) = await SendAsync(token, HttpMethod.Post,
            $"/seller-promotions/items/{Uri.EscapeDataString(publication.ExternalItemId)}?app_version=v2", body, cancellationToken);
        if (!ok)
            return (false, $"«{Short(publication.Product.Title, 40)}»: " + DescribeError(content));

        var (price, original) = ReadPrices(content);
        publication.PromotionType = type;
        publication.PromotionId = promotionId;
        publication.PromotionOfferId = ReadString(content, "offer_id") ?? offerId;
        publication.PromotionStatus = "pending";
        publication.PromotionPrice = price ?? dealPrice;
        publication.PromotionOriginalPrice = original ?? BasePrice(publication);
        publication.PromotionCheckedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return (true, $"«{Short(publication.Product.Title, 40)}» entró a {Money(publication.PromotionPrice)}.");
    }

    /// <summary>Entra con varias publicaciones (botón de Telegram o pantalla de Promociones).</summary>
    public async Task<(int Joined, List<string> Errors)> JoinManyAsync(Guid org, PromotionJoinPayload payload, CancellationToken cancellationToken = default)
    {
        var joined = 0;
        var errors = new List<string>();
        foreach (var item in payload.Items)
        {
            var (ok, message) = await JoinAsync(org, item.PublicationId, payload.PromotionId, payload.Type, payload.SubType, item.Price, item.OfferId, cancellationToken);
            if (ok) joined++;
            else errors.Add(message);
        }
        return (joined, errors);
    }

    /// <summary>Saca la publicación de la promoción en la que está (vuelve a su precio normal).</summary>
    public async Task<(bool Ok, string Message)> LeaveAsync(Guid org, int publicationId, CancellationToken cancellationToken = default)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication?.ExternalItemId is null || publication.MarketplaceAccountId is null)
            return (false, "La publicación no está en Mercado Libre.");
        if (publication.PromotionType is null)
        {
            await RefreshAsync(org, publicationId, cancellationToken);
            if (publication.PromotionType is null)
                return (false, "Esa publicación no está en ninguna promoción.");
        }
        var token = await TokenAsync(org, publication.MarketplaceAccountId.Value);
        if (token is null)
            return (false, "No hay sesión de Mercado Libre para esa cuenta.");

        var query = new StringBuilder("?promotion_type=").Append(Uri.EscapeDataString(publication.PromotionType!));
        if (!string.IsNullOrWhiteSpace(publication.PromotionId))
            query.Append("&promotion_id=").Append(Uri.EscapeDataString(publication.PromotionId));
        if (!string.IsNullOrWhiteSpace(publication.PromotionOfferId))
            query.Append("&offer_id=").Append(Uri.EscapeDataString(publication.PromotionOfferId));
        query.Append("&app_version=v2");

        var (ok, content) = await SendAsync(token, HttpMethod.Delete,
            $"/seller-promotions/items/{Uri.EscapeDataString(publication.ExternalItemId)}{query}", null, cancellationToken);
        if (!ok)
            return (false, "Mercado Libre no la quitó: " + DescribeError(content));

        var label = TypeLabel(publication.PromotionType);
        var original = publication.PromotionOriginalPrice;
        ClearActive(publication);
        publication.PromotionCheckedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return (true, $"Quité «{Short(publication.Product.Title, 40)}» de la promoción ({label})" + (original is > 0 ? $"; vuelve a {Money(original)}." : "."));
    }

    // ─────────────────────────────────────────────────────────────
    // Lectura de la API
    // ─────────────────────────────────────────────────────────────

    private async Task<List<MlPromotion>> SellerPromotionsAsync(string token, MarketplaceAccount account, CancellationToken cancellationToken)
    {
        var (ok, content) = await SendAsync(token, HttpMethod.Get,
            $"/seller-promotions/users/{Uri.EscapeDataString(account.ExternalAccountId)}?app_version=v2", null, cancellationToken);
        var list = new List<MlPromotion>();
        if (!ok)
            return list;
        try
        {
            using var document = JsonDocument.Parse(content);
            var results = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement
                : document.RootElement.TryGetProperty("results", out var r) ? r : default;
            if (results.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var p in results.EnumerateArray())
            {
                var id = Str(p, "id");
                var type = Str(p, "type");
                if (id is null || type is null)
                    continue;
                list.Add(new MlPromotion(id, type, Str(p, "sub_type"), Str(p, "status") ?? "pending",
                    Str(p, "name") ?? TypeLabel(type), Date(p, "start_date"), Date(p, "finish_date"), Date(p, "deadline_date"), account.Id));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private async Task<List<PromotionOffer>> PromotionItemsAsync(string token, MlPromotion promotion, CancellationToken cancellationToken)
    {
        var offers = new List<PromotionOffer>();
        string? searchAfter = null;
        var offset = 0;
        for (var page = 0; page < MaxItemPages; page++)
        {
            var path = $"/seller-promotions/promotions/{Uri.EscapeDataString(promotion.Id)}/items?promotion_type={Uri.EscapeDataString(promotion.Type)}&app_version=v2&limit=50" +
                       (searchAfter is not null ? "&search_after=" + Uri.EscapeDataString(searchAfter) : offset > 0 ? "&offset=" + offset : "");
            var (ok, content) = await SendAsync(token, HttpMethod.Get, path, null, cancellationToken);
            if (!ok)
                break;
            var pageOffers = ParseOffers(content, itemId: null, promotion);
            offers.AddRange(pageOffers);
            if (pageOffers.Count < 50)
                break;
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("paging", out var paging))
                {
                    var next = Str(paging, "searchAfter") ?? Str(paging, "search_after");
                    if (!string.IsNullOrEmpty(next)) { searchAfter = next; continue; }
                    var total = paging.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
                    offset += 50;
                    if (offset >= total) break;
                }
                else break;
            }
            catch (JsonException) { break; }
        }
        return offers;
    }

    /// <summary>
    /// Lee ofertas de <c>/seller-promotions/items/{id}</c> (una lista de promociones del item) o de
    /// <c>/seller-promotions/promotions/{id}/items</c> (una lista de items de la promoción).
    /// </summary>
    public static List<PromotionOffer> ParseOffers(string content, string? itemId, MlPromotion? defaultPromotion)
    {
        var offers = new List<PromotionOffer>();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var results = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r : default;
            if (results.ValueKind != JsonValueKind.Array)
                return offers;
            foreach (var e in results.EnumerateArray())
            {
                var offer = new PromotionOffer
                {
                    ItemId = defaultPromotion is null ? itemId ?? string.Empty : Str(e, "id") ?? string.Empty,
                    PromotionId = defaultPromotion?.Id ?? Str(e, "id"),
                    Type = defaultPromotion?.Type ?? Str(e, "type") ?? string.Empty,
                    SubType = defaultPromotion?.SubType ?? Str(e, "sub_type"),
                    Name = defaultPromotion?.Name ?? Str(e, "name"),
                    Status = Str(e, "status") ?? string.Empty,
                    Price = Dec(e, "price") is > 0 ? Dec(e, "price") : Dec(e, "new_price"),
                    OriginalPrice = Dec(e, "original_price"),
                    MinPrice = Dec(e, "min_discounted_price"),
                    MaxPrice = Dec(e, "max_discounted_price"),
                    SuggestedPrice = Dec(e, "suggested_discounted_price"),
                    MeliPercent = Dec(e, "meli_percentage") ?? Dec(e, "meli_percent"),
                    SellerPercent = Dec(e, "seller_percentage") ?? Dec(e, "seller_percent"),
                    OfferId = Str(e, "offer_id") ?? Str(e, "ref_id"),
                    Start = Date(e, "start_date"),
                    End = Date(e, "end_date") ?? Date(e, "finish_date")
                };
                // Campañas de ML donde el id de la promoción viene como ref (descuentos individuales no tienen id).
                if (offer.Type == "PRICE_DISCOUNT" && defaultPromotion is null)
                    offer.PromotionId = null;
                if (!string.IsNullOrEmpty(offer.ItemId) && !string.IsNullOrEmpty(offer.Type))
                    offers.Add(offer);
            }
        }
        catch (JsonException) { }
        return offers;
    }

    private static void ApplyActive(MarketplacePublication publication, PromotionOffer offer)
    {
        publication.PromotionType = offer.Type;
        publication.PromotionId = offer.Type == "PRICE_DISCOUNT" ? null : offer.PromotionId;
        publication.PromotionOfferId = offer.OfferId;
        publication.PromotionStatus = offer.Status == "sync_requested" ? "pending" : offer.Status;
        publication.PromotionPrice = offer.Price is > 0 ? offer.Price : publication.PromotionPrice;
        publication.PromotionOriginalPrice = offer.OriginalPrice is > 0 ? offer.OriginalPrice : publication.PromotionOriginalPrice ?? publication.Price;
        publication.PromotionMeliPercent = offer.MeliPercent is > 0 ? offer.MeliPercent : null;
        publication.PromotionEndsAt = offer.End;
        publication.PromotionCheckedAt = DateTime.UtcNow;
    }

    private static void ClearActive(MarketplacePublication publication)
    {
        publication.PromotionType = null;
        publication.PromotionId = null;
        publication.PromotionOfferId = null;
        publication.PromotionStatus = null;
        publication.PromotionPrice = null;
        publication.PromotionOriginalPrice = null;
        publication.PromotionMeliPercent = null;
        publication.PromotionEndsAt = null;
    }

    private Task<MarketplacePublication?> LoadAsync(Guid org, int publicationId, CancellationToken cancellationToken) =>
        _context.MarketplacePublications
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == org, cancellationToken);

    private async Task<string?> TokenAsync(Guid org, int accountId)
    {
        try { return await _auth.GetValidAccessTokenAsync(org, accountId); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No token for promotions (account {AccountId}).", accountId);
            return null;
        }
    }

    private async Task<(bool Ok, string Content)> SendAsync(string token, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("version", "v2");
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Promotions {Method} {Path} returned {Status}: {Body}", method, path, (int)response.StatusCode, content.Length > 300 ? content[..300] : content);
            return (response.IsSuccessStatusCode, content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Promotions {Method} {Path} failed.", method, path);
            return (false, string.Empty);
        }
    }

    /// <summary>Errores de la API de promociones en palabras del vendedor.</summary>
    public static string DescribeError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "Mercado Libre no respondió. Intenta de nuevo en un momento.";
        var lower = content.ToLowerInvariant();
        if (lower.Contains("credibility"))
            return "Mercado Libre dice que el precio no es creíble (el precio original subió hace poco o el descuento no es real).";
        if (lower.Contains("not_in_range") || lower.Contains("range (5, 80)"))
            return "el descuento debe ser de 5% a 80%.";
        if (lower.Contains("difference"))
            return "la diferencia entre el descuento general y el de compradores frecuentes es muy chica.";
        if (lower.Contains("reputation") || lower.Contains("reputación"))
            return "tu reputación no alcanza para esta promoción (Mercado Libre pide reputación verde).";
        if (lower.Contains("deal_price") && (lower.Contains("min") || lower.Contains("max")))
            return "ese precio está fuera de lo que permite la campaña.";
        if (lower.Contains("not_eligible") || lower.Contains("not eligible") || lower.Contains("candidate"))
            return "esta publicación no puede entrar a esa promoción.";
        return MercadoLibreListingClient.DescribeError(content);
    }

    private static (decimal? Price, decimal? Original) ReadPrices(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return (Dec(document.RootElement, "price"), Dec(document.RootElement, "original_price"));
        }
        catch (JsonException) { return (null, null); }
    }

    private static string? ReadString(string content, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return Str(document.RootElement, name);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number => v.GetRawText(),
                _ => null
            }
            : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d
              : v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var ds) ? ds : null
            : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    public static string Money(decimal? value) => value.HasValue ? value.Value.ToString("C0", Mx) : "—";

    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static TimeZoneInfo FindJuarez()
    {
        foreach (var id in new[] { "America/Ciudad_Juarez", "America/Denver", "Mountain Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }
}
