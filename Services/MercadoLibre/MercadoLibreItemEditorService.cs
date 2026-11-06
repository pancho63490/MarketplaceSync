using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed record ItemPicture(string Id, string Url);

public sealed record ItemAttribute(string Id, string Name, string? ValueName);

public sealed record ItemVariation(string Id, string Label, decimal Price, int AvailableQuantity, int SoldQuantity, string? SellerSku, string? PictureUrl);

public sealed record ItemDetails(
    int PublicationId,
    int ProductId,
    string ItemId,
    string Title,
    decimal Price,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    IReadOnlyList<string> SubStatus,
    string? Permalink,
    string ListingTypeId,
    string Condition,
    string? CategoryId,
    IReadOnlyList<ItemPicture> Pictures,
    IReadOnlyList<ItemAttribute> Attributes,
    IReadOnlyList<ItemVariation> Variations,
    string? WarrantyType,
    string? WarrantyTime,
    bool FreeShipping,
    string? ShippingMode,
    string? LogisticType,
    string? Description,
    DateTime? LastUpdated,
    bool StockControlledByApp,
    int? AppStock,
    string? AccountName)
{
    /// <summary>Mercado Libre solo deja cambiar el título mientras la publicación no tenga ventas.</summary>
    public bool CanEditTitle => SoldQuantity == 0;
    public bool IsClosed => Status == "closed";
    public bool HasVariations => Variations.Count > 0;
}

/// <summary>Lo que el vendedor cambió en el formulario. Lo que llega vacío o igual no se envía.</summary>
public sealed class ItemEdit
{
    public string? Title { get; set; }
    public string? Price { get; set; }
    public int? Quantity { get; set; }
    public string? Description { get; set; }
    public string? WarrantyType { get; set; }
    public string? WarrantyTime { get; set; }
    public string? ListingTypeId { get; set; }
    /// <summary>true cuando el formulario trae la sección de fotos (si no, las fotos no se tocan).</summary>
    public bool PicturesSubmitted { get; set; }
    /// <summary>Fotos actuales que se conservan, en el orden elegido.</summary>
    public List<string> KeepPictureIds { get; set; } = new();
    /// <summary>Links de fotos nuevas, uno por línea.</summary>
    public string? NewPictureUrls { get; set; }
    public Dictionary<string, string?> Attributes { get; set; } = new();
    public Dictionary<string, int?> VariationQuantities { get; set; } = new();
}

public sealed record EditResult(IReadOnlyList<string> Changed, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

public sealed record BulkResult(int Done, IReadOnlyList<string> Errors);

/// <summary>
/// Editar publicaciones de Mercado Libre desde la app: título, precio, stock, fotos, descripción,
/// ficha técnica, garantía, tipo de publicación, pausar/activar/finalizar y republicar.
/// Siempre lee la publicación en vivo y solo envía lo que cambió.
/// </summary>
public sealed class MercadoLibreItemEditorService
{
    public const int MaxPictures = 10;

    public static readonly IReadOnlyDictionary<string, string> ListingTypes = new Dictionary<string, string>
    {
        ["gold_special"] = "Clásica",
        ["gold_pro"] = "Premium (meses sin intereses)",
        ["free"] = "Gratuita"
    };

    public static readonly string[] WarrantyTypes = { "Garantía del vendedor", "Garantía de fábrica", "Sin garantía" };

    /// <summary>Atributos que Mercado Libre no deja cambiar desde aquí.</summary>
    private static readonly HashSet<string> LockedAttributes = new(StringComparer.OrdinalIgnoreCase) { "ITEM_CONDITION" };

    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMercadoLibreAuthService _authService;
    private readonly IMercadoLibreFeeService _feeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MercadoLibreItemEditorService> _logger;

    public MercadoLibreItemEditorService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IMercadoLibreAuthService authService,
        IMercadoLibreFeeService feeService,
        IConfiguration configuration,
        ILogger<MercadoLibreItemEditorService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _feeService = feeService;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";

    // ─────────────────────────────────────────────────────────────
    // Leer
    // ─────────────────────────────────────────────────────────────

    public async Task<ItemDetails> GetAsync(Guid organizationId, int publicationId, CancellationToken cancellationToken = default)
    {
        var (publication, token) = await LoadAsync(organizationId, publicationId, cancellationToken);
        using var item = await GetJsonAsync(token, $"/items/{Esc(publication.ExternalItemId!)}?include_attributes=all", cancellationToken);
        string? description = null;
        try
        {
            using var d = await GetJsonAsync(token, $"/items/{Esc(publication.ExternalItemId!)}/description", cancellationToken);
            description = Str(d.RootElement, "plain_text");
        }
        catch (MercadoLibreEditException)
        {
            // Sin descripción todavía.
        }

        var details = Parse(publication, item.RootElement, description);
        await RefreshLocalAsync(publication, item.RootElement, token, cancellationToken);
        return details;
    }

    private static ItemDetails Parse(MarketplacePublication publication, JsonElement root, string? description)
    {
        var pictures = new List<ItemPicture>();
        if (root.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
            foreach (var p in pics.EnumerateArray())
                if (Str(p, "id") is { } id)
                    pictures.Add(new ItemPicture(id, Str(p, "secure_url") ?? Str(p, "url") ?? string.Empty));

        var pictureById = pictures.ToDictionary(p => p.Id, p => p.Url);

        var attributes = new List<ItemAttribute>();
        if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
            foreach (var a in attrs.EnumerateArray())
                if (Str(a, "id") is { } id && !LockedAttributes.Contains(id))
                    attributes.Add(new ItemAttribute(id, Str(a, "name") ?? id, Str(a, "value_name")));

        var variations = new List<ItemVariation>();
        if (root.TryGetProperty("variations", out var vars) && vars.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vars.EnumerateArray())
            {
                var id = v.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                if (id is null) continue;
                var label = new List<string>();
                if (v.TryGetProperty("attribute_combinations", out var combos) && combos.ValueKind == JsonValueKind.Array)
                    foreach (var c in combos.EnumerateArray())
                        if (Str(c, "value_name") is { } name) label.Add(name);
                string? sku = Str(v, "seller_custom_field");
                if (sku is null && v.TryGetProperty("attributes", out var va) && va.ValueKind == JsonValueKind.Array)
                    sku = va.EnumerateArray().Where(x => Str(x, "id") == "SELLER_SKU").Select(x => Str(x, "value_name")).FirstOrDefault();
                string? picture = null;
                if (v.TryGetProperty("picture_ids", out var pids) && pids.ValueKind == JsonValueKind.Array)
                    picture = pids.EnumerateArray().Select(x => x.GetString()).Where(x => x != null && pictureById.ContainsKey(x)).Select(x => pictureById[x!]).FirstOrDefault();
                variations.Add(new ItemVariation(id, label.Count == 0 ? "Variación " + id : string.Join(" · ", label),
                    Dec(v, "price") ?? 0, Int(v, "available_quantity") ?? 0, Int(v, "sold_quantity") ?? 0, sku, picture));
            }
        }

        string? warrantyType = null, warrantyTime = null;
        if (root.TryGetProperty("sale_terms", out var terms) && terms.ValueKind == JsonValueKind.Array)
            foreach (var t in terms.EnumerateArray())
            {
                if (Str(t, "id") == "WARRANTY_TYPE") warrantyType = Str(t, "value_name");
                if (Str(t, "id") == "WARRANTY_TIME") warrantyTime = Str(t, "value_name");
            }

        bool freeShipping = false;
        string? mode = null, logistic = null;
        if (root.TryGetProperty("shipping", out var ship) && ship.ValueKind == JsonValueKind.Object)
        {
            freeShipping = ship.TryGetProperty("free_shipping", out var fs) && fs.ValueKind == JsonValueKind.True;
            mode = Str(ship, "mode");
            logistic = Str(ship, "logistic_type");
        }

        var subStatus = new List<string>();
        if (root.TryGetProperty("sub_status", out var ss) && ss.ValueKind == JsonValueKind.Array)
            subStatus.AddRange(ss.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).Cast<string>());

        var appVariants = publication.Product.Variants.Where(v => v.IsActive).ToList();
        return new ItemDetails(
            publication.Id,
            publication.ProductId,
            publication.ExternalItemId!,
            Str(root, "title") ?? publication.Product.Title ?? string.Empty,
            Dec(root, "price") ?? publication.Price ?? 0,
            Int(root, "available_quantity") ?? 0,
            Int(root, "sold_quantity") ?? 0,
            Str(root, "status") ?? "active",
            subStatus,
            Str(root, "permalink"),
            Str(root, "listing_type_id") ?? "gold_special",
            Str(root, "condition") ?? "new",
            Str(root, "category_id"),
            pictures,
            attributes,
            variations,
            warrantyType,
            warrantyTime,
            freeShipping,
            mode,
            logistic,
            description,
            DateTime.TryParse(Str(root, "last_updated"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var lu) ? lu : null,
            appVariants.Count > 0,
            appVariants.Count > 0 ? appVariants.Sum(v => Math.Max(0, v.Stock)) : null,
            publication.MarketplaceAccount?.DisplayName);
    }

    // ─────────────────────────────────────────────────────────────
    // Guardar cambios
    // ─────────────────────────────────────────────────────────────

    public async Task<EditResult> UpdateAsync(Guid organizationId, int publicationId, ItemEdit edit, CancellationToken cancellationToken = default)
    {
        var (publication, token) = await LoadAsync(organizationId, publicationId, cancellationToken);
        var itemId = publication.ExternalItemId!;
        ItemDetails current;
        using (var item = await GetJsonAsync(token, $"/items/{Esc(itemId)}?include_attributes=all", cancellationToken))
        {
            string? currentDescription = null;
            try
            {
                using var d = await GetJsonAsync(token, $"/items/{Esc(itemId)}/description", cancellationToken);
                currentDescription = Str(d.RootElement, "plain_text");
            }
            catch (MercadoLibreEditException) { }
            current = Parse(publication, item.RootElement, currentDescription);
        }

        var changed = new List<string>();
        var errors = new List<string>();
        var body = new Dictionary<string, object?>();

        // Título
        var title = edit.Title?.Trim();
        if (!string.IsNullOrEmpty(title) && title != current.Title)
        {
            if (!current.CanEditTitle)
                errors.Add("El título no se puede cambiar porque la publicación ya tiene ventas (regla de Mercado Libre).");
            else if (title.Length > 60)
                errors.Add($"El título tiene {title.Length} caracteres; el máximo es 60.");
            else
            {
                body["title"] = title;
                changed.Add("título");
            }
        }

        // Precio
        decimal? newPrice = null;
        if (!string.IsNullOrWhiteSpace(edit.Price))
        {
            if (!TryParseMoney(edit.Price, out var price) || price < 1)
                errors.Add("Escribe el precio como número, por ejemplo 1299.");
            else if (price != current.Price)
                newPrice = price;
        }

        // Stock y variaciones
        if (current.HasVariations)
        {
            var variationChanges = new List<object>();
            foreach (var v in current.Variations)
            {
                var qty = current.StockControlledByApp ? null : edit.VariationQuantities.GetValueOrDefault(v.Id);
                var qtyChanged = qty.HasValue && qty.Value >= 0 && qty.Value != v.AvailableQuantity;
                if (!qtyChanged && newPrice is null)
                    continue;
                var change = new Dictionary<string, object> { ["id"] = long.TryParse(v.Id, out var n) ? n : v.Id };
                if (qtyChanged) change["available_quantity"] = qty!.Value;
                if (newPrice is not null) change["price"] = newPrice.Value;
                variationChanges.Add(change);
            }
            if (variationChanges.Count > 0)
            {
                body["variations"] = variationChanges;
                if (variationChanges.Cast<Dictionary<string, object>>().Any(x => x.ContainsKey("available_quantity")))
                    changed.Add("stock");
            }
            if (newPrice is not null) changed.Add("precio");
        }
        else
        {
            if (newPrice is not null)
            {
                body["price"] = newPrice.Value;
                changed.Add("precio");
            }
            if (!current.StockControlledByApp && edit.Quantity is { } qty && qty >= 0 && qty != current.AvailableQuantity)
            {
                body["available_quantity"] = qty;
                changed.Add("stock");
            }
        }

        // Fotos
        var newUrls = (edit.NewPictureUrls ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .Distinct()
            .ToList();
        var keep = edit.KeepPictureIds.Where(id => current.Pictures.Any(p => p.Id == id)).Distinct().ToList();
        var picturesChanged = edit.PicturesSubmitted &&
                              (newUrls.Count > 0 || !keep.SequenceEqual(current.Pictures.Select(p => p.Id)));
        if (picturesChanged)
        {
            if (keep.Count + newUrls.Count == 0)
                errors.Add("La publicación necesita al menos una foto.");
            else if (keep.Count + newUrls.Count > MaxPictures)
                errors.Add($"Mercado Libre acepta hasta {MaxPictures} fotos; quita algunas.");
            else
            {
                body["pictures"] = keep.Select(id => (object)new { id }).Concat(newUrls.Select(u => (object)new { source = u })).ToArray();
                changed.Add("fotos");
            }
        }

        // Ficha técnica
        var attributeChanges = new List<object>();
        foreach (var (id, raw) in edit.Attributes)
        {
            var value = raw?.Trim();
            var existing = current.Attributes.FirstOrDefault(a => a.Id == id);
            if (existing is null || string.IsNullOrEmpty(value) || value == existing.ValueName)
                continue;
            attributeChanges.Add(new { id, value_name = value });
        }
        if (attributeChanges.Count > 0)
        {
            body["attributes"] = attributeChanges;
            changed.Add("ficha técnica");
        }

        // Garantía
        var warrantyType = edit.WarrantyType?.Trim();
        var warrantyTime = edit.WarrantyTime?.Trim();
        if (!string.IsNullOrEmpty(warrantyType) && (warrantyType != current.WarrantyType || (warrantyTime ?? "") != (current.WarrantyTime ?? "")))
        {
            var terms = new List<object> { new { id = "WARRANTY_TYPE", value_name = warrantyType } };
            if (warrantyType != "Sin garantía")
            {
                if (string.IsNullOrEmpty(warrantyTime))
                    errors.Add("Escribe cuánto dura la garantía, por ejemplo “30 días” o “6 meses”.");
                else
                    terms.Add(new { id = "WARRANTY_TIME", value_name = warrantyTime });
            }
            body["sale_terms"] = terms;
            changed.Add("garantía");
        }

        if (errors.Count > 0)
            return new EditResult(Array.Empty<string>(), errors);

        if (body.Count > 0)
        {
            var error = await SendAsync(token, HttpMethod.Put, $"/items/{Esc(itemId)}", body, cancellationToken);
            if (error is not null)
                return new EditResult(Array.Empty<string>(), new[] { "Mercado Libre no aceptó los cambios: " + error });
        }

        // Descripción (recurso aparte)
        var description = edit.Description?.Replace("\r\n", "\n").Trim();
        if (description is not null && description != (current.Description ?? string.Empty).Trim())
        {
            if (description.Length == 0)
                errors.Add("La descripción no puede quedar vacía.");
            else
            {
                var error = await SendAsync(token, HttpMethod.Put, $"/items/{Esc(itemId)}/description", new { plain_text = description }, cancellationToken);
                if (error is not null)
                    error = await SendAsync(token, HttpMethod.Post, $"/items/{Esc(itemId)}/description", new { plain_text = description }, cancellationToken);
                if (error is null) changed.Add("descripción");
                else errors.Add("La descripción no se guardó: " + error);
            }
        }

        // Tipo de publicación
        if (!string.IsNullOrWhiteSpace(edit.ListingTypeId) && edit.ListingTypeId != current.ListingTypeId && ListingTypes.ContainsKey(edit.ListingTypeId))
        {
            var error = await SendAsync(token, HttpMethod.Post, $"/items/{Esc(itemId)}/listing_type", new { id = edit.ListingTypeId }, cancellationToken);
            if (error is null) changed.Add("tipo de publicación");
            else errors.Add("No se pudo cambiar el tipo de publicación: " + error);
        }

        await RefreshAfterChangeAsync(publication, token, cancellationToken);
        return new EditResult(changed, errors);
    }

    /// <summary>active = activar, paused = pausar, closed = finalizar (no se puede deshacer; se republica).</summary>
    public async Task<string?> SetStatusAsync(Guid organizationId, int publicationId, string status, CancellationToken cancellationToken = default)
    {
        if (status is not ("active" or "paused" or "closed"))
            return "Estado no válido.";
        var (publication, token) = await LoadAsync(organizationId, publicationId, cancellationToken);
        var error = await SendAsync(token, HttpMethod.Put, $"/items/{Esc(publication.ExternalItemId!)}", new { status }, cancellationToken);
        if (error is null)
            await RefreshAfterChangeAsync(publication, token, cancellationToken);
        return error;
    }

    /// <summary>Republica una publicación finalizada: Mercado Libre crea una nueva con otro número.</summary>
    public async Task<(string? NewItemId, string? Error)> RelistAsync(
        Guid organizationId, int publicationId, decimal price, int quantity, CancellationToken cancellationToken = default)
    {
        var (publication, token) = await LoadAsync(organizationId, publicationId, cancellationToken);
        var client = _httpClientFactory.CreateClient();
        using var request = Request(token, HttpMethod.Post, $"/items/{Esc(publication.ExternalItemId!)}/relist",
            new { price, quantity, listing_type_id = publication.ListingTypeId ?? "gold_special" });
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (null, MercadoLibreListingClient.DescribeError(content));

        using var doc = JsonDocument.Parse(content);
        var newId = Str(doc.RootElement, "id");
        if (string.IsNullOrWhiteSpace(newId))
            return (null, "Mercado Libre no devolvió el número de la nueva publicación.");

        publication.ExternalItemId = newId;
        publication.Permalink = Str(doc.RootElement, "permalink") ?? publication.Permalink;
        publication.SoldQuantity = 0;
        publication.Variants.Clear();
        await RefreshAfterChangeAsync(publication, token, cancellationToken);
        return (newId, null);
    }

    /// <summary>Acciones en varias publicaciones: pausar, activar o cambiar el precio (porcentaje o monto).</summary>
    public async Task<BulkResult> BulkAsync(
        Guid organizationId, IReadOnlyList<int> publicationIds, string action, decimal? amount, bool percent, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var done = 0;
        foreach (var id in publicationIds.Distinct().Take(100))
        {
            try
            {
                string? error;
                if (action is "paused" or "active")
                {
                    error = await SetStatusAsync(organizationId, id, action, cancellationToken);
                }
                else if (action == "price" && amount is { } value && value != 0)
                {
                    if (percent && (value < -50 || value > 100))
                        return new BulkResult(0, new[] { "Para cuidar tus precios, el cambio en bloque va de -50% a +100%." });
                    var (publication, _) = await LoadAsync(organizationId, id, cancellationToken);
                    var currentPrice = publication.Price ?? 0;
                    if (currentPrice <= 0) { errors.Add($"{publication.ExternalItemId}: sin precio actual."); continue; }
                    var target = percent ? currentPrice * (1 + value / 100m) : currentPrice + value;
                    target = Math.Round(target, 0, MidpointRounding.AwayFromZero);
                    if (target < 1) { errors.Add($"{publication.ExternalItemId}: el precio quedaría en cero."); continue; }
                    var result = await UpdateAsync(organizationId, id, new ItemEdit { Price = target.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
                    error = result.Ok ? null : string.Join(" ", result.Errors);
                }
                else
                {
                    return new BulkResult(0, new[] { "Elige qué hacer con las publicaciones seleccionadas." });
                }

                if (error is null) done++;
                else errors.Add(await LabelAsync(id, cancellationToken) + ": " + error);
            }
            catch (MercadoLibreEditException exception)
            {
                errors.Add(await LabelAsync(id, cancellationToken) + ": " + exception.Message);
            }
        }
        return new BulkResult(done, errors);
    }

    // ─────────────────────────────────────────────────────────────
    // Apoyo
    // ─────────────────────────────────────────────────────────────

    private async Task<string> LabelAsync(int publicationId, CancellationToken cancellationToken) =>
        await _context.MarketplacePublications.AsNoTracking().Where(p => p.Id == publicationId)
            .Select(p => p.ExternalItemId).FirstOrDefaultAsync(cancellationToken) ?? $"#{publicationId}";

    private async Task<(MarketplacePublication Publication, string Token)> LoadAsync(Guid organizationId, int publicationId, CancellationToken cancellationToken)
    {
        var publication = await _context.MarketplacePublications
            .Include(p => p.Product).ThenInclude(p => p.Variants)
            .Include(p => p.MarketplaceAccount)
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == organizationId && p.Marketplace == "MercadoLibre", cancellationToken)
            ?? throw new KeyNotFoundException("Publicación no encontrada.");
        if (string.IsNullOrWhiteSpace(publication.ExternalItemId) || publication.MarketplaceAccountId is null)
            throw new MercadoLibreEditException("Esta publicación todavía no está en Mercado Libre.");

        try
        {
            var token = await _authService.GetValidAccessTokenAsync(organizationId, publication.MarketplaceAccountId.Value);
            return (publication, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MercadoLibreEditException("La cuenta de Mercado Libre de esta publicación está desconectada. Vuelve a conectarla en Cuentas de Mercado Libre.");
        }
    }

    private async Task RefreshAfterChangeAsync(MarketplacePublication publication, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var item = await GetJsonAsync(token, $"/items/{Esc(publication.ExternalItemId!)}", cancellationToken);
            await RefreshLocalAsync(publication, item.RootElement, token, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Could not refresh item {ItemId} after editing.", publication.ExternalItemId);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Copia a la app lo que ahora tiene Mercado Libre (precio, stock, estado, título, foto, comisión).</summary>
    private async Task RefreshLocalAsync(MarketplacePublication publication, JsonElement root, string token, CancellationToken cancellationToken)
    {
        var price = Dec(root, "price");
        publication.Price = price ?? publication.Price;
        publication.Stock = Int(root, "available_quantity") ?? publication.Stock;
        publication.SoldQuantity = Int(root, "sold_quantity") ?? publication.SoldQuantity;
        publication.Status = Str(root, "status") ?? publication.Status;
        publication.Permalink = Str(root, "permalink") ?? publication.Permalink;
        publication.ListingTypeId = Str(root, "listing_type_id") ?? publication.ListingTypeId;
        publication.CategoryId = Str(root, "category_id") ?? publication.CategoryId;
        publication.LastSyncedAt = DateTime.UtcNow;
        publication.UpdatedAt = DateTime.UtcNow;

        if (Str(root, "title") is { Length: > 0 } title && title != publication.Product.Title)
        {
            publication.Product.Title = title;
            publication.Product.UpdatedAt = DateTime.UtcNow;
        }
        if (root.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array &&
            pics.EnumerateArray().Select(p => Str(p, "secure_url") ?? Str(p, "url")).FirstOrDefault(u => !string.IsNullOrEmpty(u)) is { } first)
            publication.Product.ImageUrl = first.Length > 1000 ? first[..1000] : first;

        if (price is > 0 && publication.SaleFeeForPrice != price && !string.IsNullOrWhiteSpace(publication.CategoryId))
        {
            try
            {
                var fee = await _feeService.EstimateAsync(token, publication.CategoryId!, price.Value, "MXN", publication.ListingTypeId ?? "gold_special", cancellationToken);
                publication.SaleFeeMxn = fee.SaleFeeAmount;
                publication.SaleFeeForPrice = price;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Could not refresh fee for {ItemId}.", publication.ExternalItemId);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private HttpRequestMessage Request(string token, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<JsonDocument> GetJsonAsync(string token, string path, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = Request(token, HttpMethod.Get, path);
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new MercadoLibreEditException((int)response.StatusCode == 404
                ? "Mercado Libre ya no tiene esa publicación."
                : "Mercado Libre no respondió: " + MercadoLibreListingClient.DescribeError(content));
        return JsonDocument.Parse(content);
    }

    /// <summary>Envía un cambio; devuelve null si salió bien o el mensaje de Mercado Libre.</summary>
    private async Task<string?> SendAsync(string token, HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = Request(token, method, path, body);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return null;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Mercado Libre rejected {Method} {Path}: {Body}", method, path, content);
        return MercadoLibreListingClient.DescribeError(content);
    }

    public static bool TryParseMoney(string? text, out decimal value)
    {
        var clean = (text ?? string.Empty).Replace("$", string.Empty).Replace(" ", string.Empty).Trim();
        // "1,299" o "1,299.50" → miles con coma; "38,50" → decimal con coma.
        if (clean.Contains(',') && clean.Contains('.')) clean = clean.Replace(",", string.Empty);
        else if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"^\d{1,3}(,\d{3})+$")) clean = clean.Replace(",", string.Empty);
        else clean = clean.Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}

public sealed class MercadoLibreEditException : Exception
{
    public MercadoLibreEditException(string message) : base(message) { }
}
