using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.MarketInsights;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MarketInsights;

public sealed record CompetitorItem(string ItemId, string Title, decimal? Price, int? Sold, string? Condition, string? Thumbnail, string? Permalink, string? CategoryId, bool FreeShipping);

public sealed record CompetitorScanResult(int Categories, int Stores, IReadOnlyList<string> Errors);

/// <summary>
/// Competencia: encuentra qué tiendas aparecen más en los más vendidos de tus categorías (/highlights → vendedor de cada
/// publicación), guarda su reputación y deja ver sus productos (/sites/MLM/search?seller_id=) para copiarlos con mercancía importada.
/// </summary>
public sealed class CompetitorService
{
    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CompetitorService> _logger;

    public CompetitorService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<CompetitorService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";

    /// <summary>Recorre los más vendidos de las categorías vigiladas (máx. 25) y cuenta en cuántos aparece cada tienda.</summary>
    public async Task<CompetitorScanResult> ScanLeadersAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var token = await TokenAsync(org, cancellationToken)
                    ?? throw new InvalidOperationException("Conecta tu cuenta de Mercado Libre para ver a la competencia.");
        var categories = await _context.WatchedCategories.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.Enabled && (c.IsLeaf || c.IsSelected))
            .OrderByDescending(c => c.IsLeaf).ThenByDescending(c => c.LastItemsFound ?? 0)
            .Select(c => new { c.CategoryId, c.Name })
            .Take(25)
            .ToListAsync(cancellationToken);
        if (categories.Count == 0)
            throw new InvalidOperationException("Primero elige categorías en Categorías a vigilar: de ahí salen los líderes.");

        var mySellerIds = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre").Select(a => a.ExternalAccountId).ToListAsync(cancellationToken);
        var tally = new Dictionary<string, (int Count, int Best, HashSet<string> Cats)>();
        var errors = new List<string>();

        foreach (var category in categories)
        {
            try
            {
                using var highlights = await GetAsync(token, $"/highlights/MLM/category/{Uri.EscapeDataString(category.CategoryId)}", cancellationToken);
                if (highlights is null || !highlights.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                var entries = content.EnumerateArray()
                    .Select(e => (Id: Str(e, "id"), Type: Str(e, "type"), Position: Int(e, "position") ?? 0))
                    .Where(e => e.Id is not null).Take(20).ToList();

                var sellers = new List<(string SellerId, int Position)>();
                var itemIds = entries.Where(e => e.Type == "ITEM").ToList();
                foreach (var chunk in itemIds.Chunk(20))
                {
                    using var multi = await GetAsync(token, "/items?ids=" + string.Join(',', chunk.Select(c => c.Id)) + "&attributes=id,seller_id", cancellationToken);
                    if (multi?.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var row in multi.RootElement.EnumerateArray())
                    {
                        var body = row.TryGetProperty("body", out var b) ? b : row;
                        var id = Str(body, "id");
                        var seller = body.TryGetProperty("seller_id", out var s) ? s.ToString() : null;
                        var pos = chunk.FirstOrDefault(c => c.Id == id).Position;
                        if (seller is not null) sellers.Add((seller, pos));
                    }
                }
                foreach (var product in entries.Where(e => e.Type is "PRODUCT" or "USER_PRODUCT").Take(10))
                {
                    var path = product.Type == "PRODUCT" ? $"/products/{product.Id}" : $"/user-products/{product.Id}";
                    using var doc = await GetAsync(token, path, cancellationToken);
                    if (doc is null) continue;
                    var root = doc.RootElement;
                    var seller = root.TryGetProperty("buy_box_winner", out var bb) && bb.ValueKind == JsonValueKind.Object && bb.TryGetProperty("seller_id", out var bs)
                        ? bs.ToString()
                        : root.TryGetProperty("user_id", out var u) ? u.ToString() : null;
                    if (seller is not null) sellers.Add((seller, product.Position));
                }

                foreach (var (sellerId, position) in sellers)
                {
                    if (mySellerIds.Contains(sellerId)) continue;
                    var t = tally.TryGetValue(sellerId, out var existing) ? existing : (0, int.MaxValue, new HashSet<string>());
                    t.Item3.Add(category.Name);
                    tally[sellerId] = (t.Item1 + 1, Math.Min(t.Item2, position == 0 ? int.MaxValue : position), t.Item3);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Competitor scan failed for {Category}.", category.CategoryId);
                errors.Add(category.Name);
            }
        }

        // Reinicia el conteo y guarda los 30 que más aparecen.
        var stores = await _context.CompetitorStores.Where(c => c.OrganizationId == org).ToListAsync(cancellationToken);
        foreach (var s in stores) s.TopAppearances = 0;
        foreach (var (sellerId, t) in tally.OrderByDescending(x => x.Value.Count).Take(30))
        {
            var store = stores.FirstOrDefault(s => s.SellerId == sellerId);
            if (store is null)
            {
                store = new CompetitorStore { OrganizationId = org, SellerId = sellerId };
                _context.CompetitorStores.Add(store);
                stores.Add(store);
            }
            store.TopAppearances = t.Count;
            store.BestPosition = t.Best == int.MaxValue ? null : t.Best;
            store.Categories = string.Join(", ", t.Cats.Take(6)) is { Length: > 500 } c ? c[..500] : string.Join(", ", t.Cats.Take(6));
            store.LastScannedAt = DateTime.UtcNow;
            await FillSellerAsync(store, token, cancellationToken);
        }
        await _context.SaveChangesAsync(cancellationToken);
        return new CompetitorScanResult(categories.Count, tally.Count, errors);
    }

    /// <summary>Seguir una tienda por su usuario, el link de su perfil o el link de una de sus publicaciones.</summary>
    public async Task<(CompetitorStore? Store, string? Error)> FollowAsync(Guid org, string input, CancellationToken cancellationToken = default)
    {
        var token = await TokenAsync(org, cancellationToken);
        if (token is null)
            return (null, "Conecta tu cuenta de Mercado Libre primero.");
        input = input.Trim();
        string? sellerId = null;

        var itemMatch = Regex.Match(input, @"MLM-?(\d{6,})", RegexOptions.IgnoreCase);
        if (itemMatch.Success)
        {
            using var item = await GetAsync(token, $"/items/MLM{itemMatch.Groups[1].Value}", cancellationToken);
            sellerId = item is not null && item.RootElement.TryGetProperty("seller_id", out var s) ? s.ToString() : null;
            if (sellerId is null)
                return (null, "No encontré esa publicación en Mercado Libre.");
        }
        else
        {
            var nickname = Regex.Match(input, @"perfil/([^/?#\s]+)", RegexOptions.IgnoreCase) is { Success: true } m
                ? Uri.UnescapeDataString(m.Groups[1].Value)
                : input.TrimStart('@');
            using var search = await GetAsync(token, $"/sites/MLM/search?nickname={Uri.EscapeDataString(nickname)}&limit=1", cancellationToken);
            if (search is not null && search.RootElement.TryGetProperty("seller", out var seller) && seller.TryGetProperty("id", out var sid))
                sellerId = sid.ToString();
            else if (search is not null && search.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0
                     && results[0].TryGetProperty("seller", out var s2) && s2.TryGetProperty("id", out var sid2))
                sellerId = sid2.ToString();
            if (sellerId is null)
                return (null, "No encontré esa tienda. Pega el link de una de sus publicaciones (el que empieza con articulo.mercadolibre.com.mx).");
        }

        var store = await _context.CompetitorStores.FirstOrDefaultAsync(c => c.OrganizationId == org && c.SellerId == sellerId, cancellationToken);
        if (store is null)
        {
            store = new CompetitorStore { OrganizationId = org, SellerId = sellerId };
            _context.CompetitorStores.Add(store);
        }
        store.Followed = true;
        store.Hidden = false;
        await FillSellerAsync(store, token, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return (store, null);
    }

    /// <summary>Productos activos de la tienda (los que Mercado Libre devuelve en su búsqueda), con filtro de texto.</summary>
    public async Task<(IReadOnlyList<CompetitorItem> Items, int Total, string? Error)> GetItemsAsync(Guid org, CompetitorStore store, string? query, int offset, CancellationToken cancellationToken = default)
    {
        var token = await TokenAsync(org, cancellationToken);
        if (token is null)
            return (Array.Empty<CompetitorItem>(), 0, "Conecta tu cuenta de Mercado Libre primero.");
        var q = string.IsNullOrWhiteSpace(query) ? "" : "&q=" + Uri.EscapeDataString(query.Trim());
        try
        {
            using var doc = await GetAsync(token, $"/sites/MLM/search?seller_id={Uri.EscapeDataString(store.SellerId)}{q}&limit=50&offset={Math.Max(0, offset)}", cancellationToken);
            if (doc is null || !doc.RootElement.TryGetProperty("results", out var results))
                return (Array.Empty<CompetitorItem>(), 0, "Mercado Libre no devolvió los productos de esta tienda.");
            var total = doc.RootElement.TryGetProperty("paging", out var paging) ? Int(paging, "total") ?? 0 : 0;
            var items = results.EnumerateArray().Select(r => new CompetitorItem(
                Str(r, "id") ?? "",
                Str(r, "title") ?? "",
                Dec(r, "price"),
                Int(r, "sold_quantity"),
                Str(r, "condition"),
                (Str(r, "thumbnail") ?? "").Replace("http://", "https://"),
                Str(r, "permalink"),
                Str(r, "category_id"),
                r.TryGetProperty("shipping", out var sh) && sh.TryGetProperty("free_shipping", out var fs) && fs.ValueKind == JsonValueKind.True))
                .OrderByDescending(i => i.Sold ?? 0)
                .ToList();
            return (items, total, null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Competitor items failed for {Seller}.", store.SellerId);
            return (Array.Empty<CompetitorItem>(), 0, "Mercado Libre no respondió; intenta en un momento.");
        }
    }

    private async Task FillSellerAsync(CompetitorStore store, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var user = await GetAsync(token, $"/users/{Uri.EscapeDataString(store.SellerId)}", cancellationToken);
            if (user is null) return;
            var root = user.RootElement;
            store.Nickname = Str(root, "nickname") ?? store.Nickname;
            store.Permalink = Str(root, "permalink") ?? store.Permalink;
            if (root.TryGetProperty("seller_reputation", out var rep) && rep.ValueKind == JsonValueKind.Object)
            {
                store.PowerSeller = Str(rep, "power_seller_status");
                store.ReputationLevel = Str(rep, "level_id") is { Length: > 0 } level && int.TryParse(level[..1], out var l) ? l : null;
                store.TotalSales = rep.TryGetProperty("transactions", out var tx) ? Int(tx, "completed") ?? Int(tx, "total") : null;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Seller info failed for {Seller}.", store.SellerId);
        }
        if (string.IsNullOrWhiteSpace(store.Nickname))
            store.Nickname = "Vendedor " + store.SellerId;
    }

    private async Task<string?> TokenAsync(Guid org, CancellationToken cancellationToken)
    {
        var accountId = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .Select(a => (int?)a.Id).FirstOrDefaultAsync(cancellationToken);
        if (accountId is null) return null;
        try { return await _auth.GetValidAccessTokenAsync(org, accountId.Value); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "No ML token for competitors.");
            return null;
        }
    }

    private async Task<JsonDocument?> GetAsync(string token, string path, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
