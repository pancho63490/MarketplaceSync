using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed record ListingSyncResult(
    int Accounts,
    int Listings,
    int Imported,
    int StockPushed,
    IReadOnlyList<string> Warnings);

public sealed record StockPushResult(int Publications, int Updated, IReadOnlyList<string> Errors);

public interface IMercadoLibreListingSyncService
{
    /// <summary>Lee de Mercado Libre las publicaciones activas y pausadas de cada cuenta conectada.</summary>
    Task<ListingSyncResult> SyncOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Sincroniza todas las organizaciones con cuentas conectadas (trabajo programado).</summary>
    Task<int> SyncAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Envía a Mercado Libre el stock del inventario de la app para las publicaciones del producto.</summary>
    Task<StockPushResult> PushStockAsync(int productId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sincronización con Mercado Libre.
///
/// Fuente de verdad del stock:
/// - Si el producto tiene SKU activos en la app, manda el inventario de la app: el stock se envía a Mercado Libre.
/// - Si no tiene SKU (publicaciones hechas fuera de la app), manda Mercado Libre: solo se lee.
/// Así nunca se pausa una publicación por un inventario que la app no controla.
/// </summary>
public sealed class MercadoLibreListingSyncService : IMercadoLibreListingSyncService
{
    private const int MaxListingsPerAccount = 1000;
    private const int MaxFeeLookupsPerSync = 60;

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMercadoLibreAuthService _authService;
    private readonly IMercadoLibreFeeService _feeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MercadoLibreListingSyncService> _logger;

    public MercadoLibreListingSyncService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IMercadoLibreAuthService authService,
        IMercadoLibreFeeService feeService,
        IConfiguration configuration,
        ILogger<MercadoLibreListingSyncService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _feeService = feeService;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase
    {
        get
        {
            var value = _configuration["MercadoLibre:ApiBaseUrl"];
            return string.IsNullOrWhiteSpace(value) ? "https://api.mercadolibre.com" : value.TrimEnd('/');
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Leer publicaciones
    // ─────────────────────────────────────────────────────────────

    public async Task<ListingSyncResult> SyncOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        int listings = 0, imported = 0, pushed = 0;

        var accounts = await _context.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre" && x.Status == "Connected")
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
            return new ListingSyncResult(0, 0, 0, 0, new[] { "Conecta una cuenta de Mercado Libre para ver tus publicaciones." });

        foreach (var account in accounts)
        {
            string token;
            try
            {
                token = await _authService.GetValidAccessTokenAsync(organizationId, account.Id);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Listing sync: no token for account {AccountId}.", account.Id);
                warnings.Add($"La cuenta {account.DisplayName ?? account.ExternalAccountId} necesita volver a conectarse.");
                continue;
            }

            try
            {
                var ids = new List<string>();
                foreach (var status in new[] { "active", "paused" })
                    ids.AddRange(await SearchItemIdsAsync(token, account.ExternalAccountId, status, cancellationToken));
                ids = ids.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxListingsPerAccount).ToList();

                var items = await GetItemsAsync(token, ids, cancellationToken);
                var result = await ApplyItemsAsync(organizationId, account.Id, token, items, cancellationToken);
                listings += result.Listings;
                imported += result.Imported;
                pushed += result.Pushed;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Listing sync failed for account {AccountId}.", account.Id);
                warnings.Add($"No se pudieron leer las publicaciones de {account.DisplayName ?? account.ExternalAccountId}: {exception.Message}");
            }
        }

        return new ListingSyncResult(accounts.Count, listings, imported, pushed, warnings);
    }

    public async Task<int> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var organizationIds = await _context.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.Marketplace == "MercadoLibre" && x.Status == "Connected")
            .Select(x => x.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var count = 0;
        foreach (var organizationId in organizationIds)
        {
            try
            {
                var result = await SyncOrganizationAsync(organizationId, cancellationToken);
                count += result.Listings;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Listing sync failed for organization {OrganizationId}.", organizationId);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }

        return count;
    }

    private sealed record ApplyResult(int Listings, int Imported, int Pushed);

    private async Task<ApplyResult> ApplyItemsAsync(
        Guid organizationId,
        int accountId,
        string token,
        IReadOnlyList<JsonElement> items,
        CancellationToken cancellationToken)
    {
        var itemIds = items.Select(i => ReadString(i, "id")).Where(x => x != null).Cast<string>().ToList();
        var publications = await _context.MarketplacePublications
            .Include(p => p.Product).ThenInclude(p => p.Variants)
            .Include(p => p.Variants)
            .Where(p => p.Product.OrganizationId == organizationId &&
                        p.Marketplace == "MercadoLibre" &&
                        p.ExternalItemId != null &&
                        itemIds.Contains(p.ExternalItemId))
            .ToListAsync(cancellationToken);
        var byItemId = publications.ToDictionary(p => p.ExternalItemId!, StringComparer.OrdinalIgnoreCase);
        var reserve = await GeneralReserveAsync(organizationId, cancellationToken);

        var skuMap = await _context.ProductVariants
            .Where(v => v.OrganizationId == organizationId && v.IsActive)
            .Select(v => new { v.Sku, v.ProductId })
            .ToListAsync(cancellationToken);
        var productBySku = skuMap
            .GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ProductId, StringComparer.OrdinalIgnoreCase);

        int imported = 0, pushed = 0, feeLookups = 0;

        foreach (var item in items)
        {
            var itemId = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(itemId))
                continue;

            var sku = ReadString(item, "seller_custom_field") ?? ReadAttribute(item, "SELLER_SKU");
            var hasVariations = item.TryGetProperty("variations", out var variations) &&
                                variations.ValueKind == JsonValueKind.Array && variations.GetArrayLength() > 0;

            if (!byItemId.TryGetValue(itemId, out var publication))
            {
                // Publicación hecha fuera de la app: se vincula al producto con el mismo SKU o se importa.
                Product? product = null;
                MarketplacePublication? reusable = null;
                if (sku != null && productBySku.TryGetValue(sku, out var productId))
                {
                    var linked = await _context.Products
                        .Include(p => p.Variants)
                        .Include(p => p.MarketplacePublications)
                        .FirstAsync(p => p.Id == productId, cancellationToken);
                    var sameAccount = linked.MarketplacePublications.FirstOrDefault(p =>
                        p.Marketplace == "MercadoLibre" && p.MarketplaceAccountId == accountId);
                    if (sameAccount is null || string.IsNullOrWhiteSpace(sameAccount.ExternalItemId))
                    {
                        // Mismo SKU: se vincula (y si había un borrador sin publicar en esa cuenta, se reutiliza).
                        product = linked;
                        reusable = sameAccount;
                    }
                }

                if (reusable is not null)
                {
                    reusable.ExternalItemId = itemId;
                    publication = reusable;
                    byItemId[itemId] = publication;
                    imported++;
                }
                else
                {
                if (product is null)
                {
                    product = new Product
                    {
                        OrganizationId = organizationId,
                        SourceUrl = ReadString(item, "permalink") ?? string.Empty,
                        SourceMarketplace = "MercadoLibre",
                        SourceProductId = itemId,
                        Title = Truncate(ReadString(item, "title"), 300),
                        ImageUrl = Truncate(BestThumbnail(item), 1000),
                        Brand = Truncate(ReadAttribute(item, "BRAND"), 200),
                        Model = Truncate(ReadAttribute(item, "MODEL"), 200),
                        SourceStatus = "ImportedFromMercadoLibre",
                        Status = "ReadyToPublish",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.Products.Add(product);
                }

                publication = new MarketplacePublication
                {
                    Product = product,
                    Marketplace = "MercadoLibre",
                    MarketplaceAccountId = accountId,
                    ExternalItemId = itemId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.MarketplacePublications.Add(publication);
                byItemId[itemId] = publication;
                imported++;
                }
            }

            var price = ReadDecimal(item, "price");
            publication.Price = price ?? publication.Price;
            publication.Stock = ReadInt(item, "available_quantity") ?? publication.Stock;
            publication.SoldQuantity = ReadInt(item, "sold_quantity") ?? publication.SoldQuantity;
            publication.Status = ReadString(item, "status") ?? publication.Status;
            publication.Permalink = ReadString(item, "permalink") ?? publication.Permalink;
            publication.CategoryId = ReadString(item, "category_id") ?? publication.CategoryId;
            publication.ListingTypeId = ReadString(item, "listing_type_id") ?? publication.ListingTypeId;
            publication.CurrencyId = ReadString(item, "currency_id") ?? publication.CurrencyId ?? "MXN";
            if (item.TryGetProperty("catalog_listing", out var catalogListing) && catalogListing.ValueKind is JsonValueKind.True or JsonValueKind.False)
                publication.CatalogListing = catalogListing.ValueKind == JsonValueKind.True;
            publication.CatalogProductId = Truncate(ReadString(item, "catalog_product_id"), 40) ?? publication.CatalogProductId;
            publication.IsPublished = true;
            publication.MarketplaceAccountId ??= accountId;
            publication.LastSyncedAt = DateTime.UtcNow;
            publication.UpdatedAt = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(publication.Product.ImageUrl))
                publication.Product.ImageUrl = Truncate(BestThumbnail(item), 1000);

            // Comisión real para el precio actual (se recalcula solo si cambió el precio).
            if (price is > 0 && publication.SaleFeeForPrice != price && feeLookups < MaxFeeLookupsPerSync &&
                !string.IsNullOrWhiteSpace(publication.CategoryId))
            {
                feeLookups++;
                try
                {
                    var fee = await _feeService.EstimateAsync(
                        token, publication.CategoryId!, price.Value, "MXN",
                        publication.ListingTypeId ?? MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DefaultListingTypeId, cancellationToken);
                    publication.SaleFeeMxn = fee.SaleFeeAmount;
                    publication.SaleFeeForPrice = price;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(exception, "Could not get fee for item {ItemId}.", itemId);
                }
            }

            // Stock: si la app controla el inventario de este producto, la app manda.
            var activeVariants = publication.Product.Variants.Where(v => v.IsActive).ToList();
            if (activeVariants.Count > 0 && !hasVariations)
            {
                var desired = MarketplaceSync.Services.Inventory.StockReserve.Publishable(activeVariants, reserve);
                if (desired != publication.Stock)
                {
                    var error = await PutAsync(token, itemId, new { available_quantity = desired }, cancellationToken);
                    RecordPush(publication, error, desired);
                    if (error == null) pushed++;
                }
            }
            else if (hasVariations && publication.Variants.Count > 0)
            {
                pushed += await PushVariationsAsync(token, publication, activeVariants, variations, reserve, cancellationToken);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new ApplyResult(items.Count, imported, pushed);
    }

    // ─────────────────────────────────────────────────────────────
    // Enviar stock
    // ─────────────────────────────────────────────────────────────

    public async Task<StockPushResult> PushStockAsync(int productId, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var product = await _context.Products
            .Include(p => p.Variants)
            .Include(p => p.MarketplacePublications).ThenInclude(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
            return new StockPushResult(0, 0, errors);

        var activeVariants = product.Variants.Where(v => v.IsActive).ToList();
        var publications = product.MarketplacePublications
            .Where(p => p.Marketplace == "MercadoLibre" && p.IsPublished && !string.IsNullOrWhiteSpace(p.ExternalItemId) && p.MarketplaceAccountId.HasValue)
            .ToList();
        if (activeVariants.Count == 0 || publications.Count == 0)
            return new StockPushResult(publications.Count, 0, errors);

        var reserve = await GeneralReserveAsync(product.OrganizationId, cancellationToken);
        var updated = 0;
        foreach (var publication in publications)
        {
            string token;
            try
            {
                token = await _authService.GetValidAccessTokenAsync(product.OrganizationId, publication.MarketplaceAccountId!.Value);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add("Cuenta de Mercado Libre desconectada: " + exception.Message);
                continue;
            }

            if (publication.Variants.Count > 0)
            {
                updated += await PushVariationsAsync(token, publication, activeVariants, default, reserve, cancellationToken);
                continue;
            }

            var desired = MarketplaceSync.Services.Inventory.StockReserve.Publishable(activeVariants, reserve);
            var error = await PutAsync(token, publication.ExternalItemId!, new { available_quantity = desired }, cancellationToken);
            RecordPush(publication, error, desired);
            if (error == null) updated++;
            else errors.Add(error);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new StockPushResult(publications.Count, updated, errors);
    }

    private async Task<int> PushVariationsAsync(
        string token,
        MarketplacePublication publication,
        IReadOnlyList<ProductVariant> variants,
        JsonElement remoteVariations,
        int reserve,
        CancellationToken cancellationToken)
    {
        var remoteQuantity = new Dictionary<string, int>();
        if (remoteVariations.ValueKind == JsonValueKind.Array)
        {
            foreach (var variation in remoteVariations.EnumerateArray())
            {
                var id = variation.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
                if (id != null)
                    remoteQuantity[id] = ReadInt(variation, "available_quantity") ?? -1;
            }
        }

        var changes = publication.Variants
            .Where(link => !string.IsNullOrWhiteSpace(link.ExternalVariationId))
            .Select(link => new
            {
                Link = link,
                Variant = variants.FirstOrDefault(v => v.Id == link.ProductVariantId)
            })
            .Where(x => x.Variant != null)
            .Select(x => new { x.Link.ExternalVariationId, Quantity = MarketplaceSync.Services.Inventory.StockReserve.Publishable(x.Variant!, reserve) })
            .Where(x => !remoteQuantity.TryGetValue(x.ExternalVariationId!, out var current) || current != x.Quantity)
            .ToList();
        if (changes.Count == 0)
            return 0;

        var body = new
        {
            variations = changes.Select(c => new
            {
                id = long.TryParse(c.ExternalVariationId, out var numeric) ? (object)numeric : c.ExternalVariationId!,
                available_quantity = c.Quantity
            }).ToArray()
        };
        var error = await PutAsync(token, publication.ExternalItemId!, body, cancellationToken);
        RecordPush(publication, error, MarketplaceSync.Services.Inventory.StockReserve.Publishable(variants, reserve));
        return error == null ? 1 : 0;
    }

    /// <summary>Reserva general de la organización (piezas por SKU que no se publican).</summary>
    private async Task<int> GeneralReserveAsync(Guid organizationId, CancellationToken cancellationToken) =>
        Math.Max(0, await _context.AutomationSettings.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId)
            .Select(a => a.StockReserve)
            .FirstOrDefaultAsync(cancellationToken));

    private static void RecordPush(MarketplacePublication publication, string? error, int quantity)
    {
        publication.LastStockPushAt = DateTime.UtcNow;
        publication.LastStockPushError = error is null ? null : Truncate(error, 500);
        if (error is null)
            publication.Stock = quantity;
    }

    // ─────────────────────────────────────────────────────────────
    // HTTP
    // ─────────────────────────────────────────────────────────────

    private async Task<List<string>> SearchItemIdsAsync(string token, string sellerId, string status, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        var client = _httpClientFactory.CreateClient();
        for (var offset = 0; offset < MaxListingsPerAccount; offset += 50)
        {
            using var document = await GetJsonAsync(client, token,
                $"{ApiBase}/users/{Uri.EscapeDataString(sellerId)}/items/search?status={status}&limit=50&offset={offset}",
                cancellationToken);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                break;

            var page = results.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
            ids.AddRange(page);

            var total = document.RootElement.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var totalElement)
                ? totalElement.GetInt32()
                : page.Count;
            if (page.Count < 50 || ids.Count >= total)
                break;
        }

        return ids;
    }

    private async Task<List<JsonElement>> GetItemsAsync(string token, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var client = _httpClientFactory.CreateClient();
        foreach (var chunk in ids.Chunk(20))
        {
            using var document = await GetJsonAsync(client, token,
                $"{ApiBase}/items?ids={string.Join(',', chunk.Select(Uri.EscapeDataString))}",
                cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("code", out var code) && code.GetInt32() == 200 &&
                    entry.TryGetProperty("body", out var body))
                    items.Add(body.Clone());
            }
        }

        return items;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string token, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Mercado Libre respondió HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return JsonDocument.Parse(content);
    }

    private async Task<string?> PutAsync(string token, string itemId, object body, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/items/{Uri.EscapeDataString(itemId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Stock push to {ItemId} failed: {Body}", itemId, content);
            return MercadoLibreListingClient.DescribeError(content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Stock push to {ItemId} failed.", itemId);
            return exception.Message;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Lectura de JSON
    // ─────────────────────────────────────────────────────────────

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static decimal? ReadDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static string? ReadAttribute(JsonElement item, string id)
    {
        if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (ReadString(attribute, "id") == id)
                return ReadString(attribute, "value_name");
        }

        return null;
    }

    private static string? BestThumbnail(JsonElement item)
    {
        if (item.TryGetProperty("pictures", out var pictures) && pictures.ValueKind == JsonValueKind.Array && pictures.GetArrayLength() > 0)
        {
            var first = pictures[0];
            var url = ReadString(first, "secure_url") ?? ReadString(first, "url");
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        var thumbnail = ReadString(item, "secure_thumbnail") ?? ReadString(item, "thumbnail");
        return thumbnail?.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
