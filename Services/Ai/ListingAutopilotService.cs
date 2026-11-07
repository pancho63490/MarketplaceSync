using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Services;
using MarketplaceSync.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Ai;

public sealed record AutopilotResult(
    int ProductId,
    bool Ready,
    bool Published,
    string? Permalink,
    IReadOnlyList<string> Done,
    IReadOnlyList<string> Missing,
    bool AiConfigured,
    string Engine);

public interface IListingAutopilotService
{
    /// <summary>
    /// Prepara la publicación con IA (categoría, título, descripción, atributos y precio)
    /// y, si se pide y todo está completo, la publica en Mercado Libre.
    /// </summary>
    Task<AutopilotResult> RunAsync(
        Guid organizationId,
        int productId,
        bool publishIfReady,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Piloto automático de publicación:
/// 1. Categoría de Mercado Libre por título (si no tiene).
/// 2. Atributos obligatorios de la categoría.
/// 3. Claude: título ≤ 60, descripción en texto plano, marca, modelo y valor de cada atributo.
/// 4. Precio con las reglas de costos de cruce.
/// 5. Guarda el borrador completo y, si todo está listo y se pidió, publica (con descripción).
/// Nunca publica si falta algo, si la IA marca riesgo de propiedad intelectual o si no hay stock.
/// </summary>
public sealed class ListingAutopilotService : IListingAutopilotService
{
    private readonly AppDbContext _context;
    private readonly MercadoLibreCategoryService _categoryService;
    private readonly MercadoLibreListingClient _listingClient;
    private readonly IProductAiEnricherService _enricher;
    private readonly ICrossBorderPricingService _pricing;
    private readonly IMercadoLibreAuthService _authService;
    private readonly ClaudeClient _claude;
    private readonly ProductTitleTranslator _titleTranslator;
    private readonly ILogger<ListingAutopilotService> _logger;
    private readonly IServiceProvider _services;

    public ListingAutopilotService(
        AppDbContext context,
        MercadoLibreCategoryService categoryService,
        MercadoLibreListingClient listingClient,
        IProductAiEnricherService enricher,
        ICrossBorderPricingService pricing,
        IMercadoLibreAuthService authService,
        ClaudeClient claude,
        ProductTitleTranslator titleTranslator,
        ILogger<ListingAutopilotService> logger,
        IServiceProvider services)
    {
        _services = services;
        _titleTranslator = titleTranslator;
        _context = context;
        _categoryService = categoryService;
        _listingClient = listingClient;
        _enricher = enricher;
        _pricing = pricing;
        _authService = authService;
        _claude = claude;
        _logger = logger;
    }

    public async Task<AutopilotResult> RunAsync(
        Guid organizationId,
        int productId,
        bool publishIfReady,
        CancellationToken cancellationToken = default)
    {
        var done = new List<string>();
        var missing = new List<string>();

        var product = await _context.Products
            .Include(x => x.MarketplacePublications)
            .Include(x => x.Variants)
            .FirstOrDefaultAsync(x => x.Id == productId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("No se encontró el producto.");

        // Cuenta de Mercado Libre conectada (la de la publicación existente o la primera).
        var existing = product.MarketplacePublications.FirstOrDefault(x => x.Marketplace == "MercadoLibre");
        var accountId = existing?.MarketplaceAccountId ?? await _context.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre" && x.Status == "Connected")
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        string? accessToken = null;
        if (accountId.HasValue)
        {
            try
            {
                accessToken = await _authService.GetValidAccessTokenAsync(organizationId, accountId.Value);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Autopilot could not get a Mercado Libre token for account {AccountId}.", accountId);
            }
        }

        var publication = existing ?? new MarketplacePublication
        {
            Product = product,
            Marketplace = "MercadoLibre",
            MarketplaceAccountId = accountId,
            CurrencyId = "MXN",
            ListingTypeId = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DefaultListingTypeId,
            Condition = "new",
            Status = "Draft",
            IsPublished = false,
            CreatedAt = DateTime.UtcNow
        };
        if (existing is null)
            _context.MarketplacePublications.Add(publication);
        publication.MarketplaceAccountId ??= accountId;

        // 0) Título en español: Mercado Libre sugiere mejor la categoría y el comprador busca en español.
        if (ProductTitleTranslator.LooksEnglish(product.Title))
        {
            product.Title = await _titleTranslator.ToSpanishAsync(product.Title, cancellationToken);
            done.Add("Título traducido al español");
        }

        // 1) Categoría
        string? categoryName = null;
        if (string.IsNullOrWhiteSpace(publication.CategoryId) && !string.IsNullOrWhiteSpace(product.Title))
        {
            try
            {
                var suggestion = (await _categoryService.SuggestCategoriesAsync(product.Title))
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Category_Id));
                if (suggestion is not null)
                {
                    publication.CategoryId = suggestion.Category_Id;
                    categoryName = suggestion.Category_Name;
                    done.Add($"Categoría: {suggestion.Category_Name ?? suggestion.Category_Id}");
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Autopilot category suggestion failed for product {Id}.", product.Id);
            }
        }

        // 2) Atributos obligatorios
        var schema = new List<MercadoLibreAttributeInput>();
        if (!string.IsNullOrWhiteSpace(publication.CategoryId))
        {
            try
            {
                schema = await _listingClient.GetRequiredAttributesAsync(publication.CategoryId, accessToken, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Autopilot could not load attributes for {CategoryId}.", publication.CategoryId);
            }
        }

        // 3) IA
        var enrichment = await _enricher.EnrichProductAsync(new ProductAiEnrichmentRequest(
            RawTitle: product.Title ?? string.Empty,
            RawDescription: product.Description,
            RawBrand: product.Brand,
            RawModel: product.Model,
            SourcePriceUsd: product.SourcePrice,
            SourceUrl: product.SourceUrl,
            SourceMarketplace: product.SourceMarketplace,
            CategoryId: publication.CategoryId ?? string.Empty,
            CategoryName: categoryName,
            // El código de barras nunca lo pone la IA: sale de la etiqueta real.
            RequiredAttributes: schema
                .Where(a => a.Id is not (MercadoLibreListingClient.GtinId or MercadoLibreListingClient.EmptyGtinReasonId))
                .Select(a => new CategoryAttributeSchema(
                    a.Id, a.Name, a.Required, a.ValueType,
                    a.Values.Select(v => new CategoryAttributeAllowedValue(v.Id ?? string.Empty, v.Name)).ToList()))
                .ToList()), cancellationToken);

        if (!string.IsNullOrWhiteSpace(enrichment.OptimizedTitle))
            product.Title = enrichment.OptimizedTitle;
        if (!string.IsNullOrWhiteSpace(enrichment.CleanDescription))
            product.Description = enrichment.CleanDescription;
        product.Brand = string.IsNullOrWhiteSpace(enrichment.Brand) ? product.Brand : enrichment.Brand;
        product.Model = string.IsNullOrWhiteSpace(enrichment.Model) ? product.Model : enrichment.Model;
        done.Add(_claude.IsConfigured && enrichment.ProcessingEngine.StartsWith("Claude", StringComparison.Ordinal)
            ? "Título y descripción escritos con IA"
            : "Título y descripción con reglas básicas (la IA no está configurada)");

        // Atributos: valor de la IA, marca/modelo conocidos o valor ya guardado.
        var saved = ReadSavedAttributes(publication.AttributesJson);
        var chosen = new List<SavedAttributeValue>();
        var barcode = product.Variants
            .Where(v => v.IsActive && !string.IsNullOrWhiteSpace(v.Barcode))
            .Select(v => MercadoLibreListingClient.ValidateGtin(v.Barcode, out var digits) is null ? digits : null)
            .FirstOrDefault(d => d is not null);
        if (barcode is null && schema.Any(a => a.Id == MercadoLibreListingClient.GtinId))
        {
            // Código real del anuncio de origen (eBay); se guarda junto con el resto del producto.
            var sourceBarcodes = _services.GetService<MarketplaceSync.Services.Products.SourceBarcodeService>();
            if (sourceBarcodes is not null)
                barcode = await sourceBarcodes.EnsureAsync(product, cancellationToken);
            barcode ??= await FindBarcodeOnlineAsync(product, cancellationToken);
        }
        if (barcode is not null && schema.Any(a => a.Id == MercadoLibreListingClient.GtinId))
            done.Add($"Código de barras {barcode} (del anuncio de origen o de la variante)");
        foreach (var attribute in schema)
        {
            if (attribute.Id == MercadoLibreListingClient.EmptyGtinReasonId)
            {
                // El motivo de "no tiene código" solo lo elige el usuario.
                var reason = saved.FirstOrDefault(x => x.Id == attribute.Id);
                if (reason is not null && barcode is null)
                    chosen.Add(reason);
                continue;
            }

            if (attribute.Id == MercadoLibreListingClient.GtinId)
            {
                var gtin = barcode is not null
                    ? new SavedAttributeValue(attribute.Id, null, barcode)
                    : saved.FirstOrDefault(x => x.Id == attribute.Id);
                var hasReason = saved.Any(x => x.Id == MercadoLibreListingClient.EmptyGtinReasonId);
                if (gtin is not null)
                    chosen.Add(gtin);
                else if (attribute.Required && !hasReason)
                    missing.Add("Código de barras (GTIN): escanéalo en Publicar o marca que no tiene");
                continue;
            }

            var value = enrichment.Attributes.GetValueOrDefault(attribute.Id)
                        ?? (attribute.Id.Equals("BRAND", StringComparison.OrdinalIgnoreCase) ? product.Brand : null)
                        ?? (attribute.Id is "MODEL" or "PART_NUMBER" or "MPN" ? product.Model : null);

            var resolved = ResolveAttribute(attribute, value)
                           ?? saved.FirstOrDefault(x => x.Id.Equals(attribute.Id, StringComparison.OrdinalIgnoreCase));
            if (resolved is not null)
                chosen.Add(resolved);
            else if (attribute.Required)
                missing.Add($"Atributo obligatorio: {attribute.Name}");
        }
        var requiredCount = schema.Count(a => a.Required);
        if (requiredCount > 0 && schema.Where(a => a.Required).All(a => chosen.Any(c => c.Id == a.Id)))
            done.Add($"{requiredCount} atributos obligatorios completos");
        publication.AttributesJson = JsonSerializer.Serialize(chosen);

        // 4) Precio y stock
        if (publication.Price is not > 0)
        {
            var isMxn = string.Equals(product.SourceCurrency, "MXN", StringComparison.OrdinalIgnoreCase);
            if (product.SourcePrice is > 0 && !isMxn)
            {
                var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
                var breakdown = _pricing.CalculatePricing(product.SourcePrice.Value, settings);
                publication.Price = breakdown.SuggestedListingPriceMxn;
                var mx = System.Globalization.CultureInfo.GetCultureInfo("es-MX");
                done.Add($"Precio sugerido {breakdown.SuggestedListingPriceMxn.ToString("C0", mx)} (ganancia {breakdown.EffectiveNetProfitMxn.ToString("C0", mx)})");
            }
            else if (product.SourcePrice is > 0)
            {
                publication.Price = product.SourcePrice;
            }
        }

        var activeVariants = product.Variants.Where(v => v.IsActive).ToList();
        var hasVariations = activeVariants.Any(v => v.AttributesJson is not null && v.AttributesJson.Trim() != "{}");
        var stock = activeVariants.Count > 0
            ? activeVariants.Sum(v => Math.Max(0, v.Stock))
            : publication.Stock ?? product.SourceStock ?? 1;
        publication.Stock = activeVariants.Count > 0 ? stock : Math.Max(1, stock);

        // 5) Qué falta
        if (string.IsNullOrWhiteSpace(publication.CategoryId)) missing.Add("Categoría de Mercado Libre");
        if (string.IsNullOrWhiteSpace(product.Title)) missing.Add("Título");
        if (string.IsNullOrWhiteSpace(product.ImageUrl)) missing.Add("Imagen del producto");
        if (publication.Price is not > 0) missing.Add("Precio");
        if (publication.Stock is not > 0) missing.Add("Stock: recibe la mercancía o define cuántas piezas tienes");
        if (enrichment.HighRiskIntellectualProperty)
            missing.Add("Revisión manual: la IA detectó posible riesgo de marca o réplica" +
                        (string.IsNullOrWhiteSpace(enrichment.IntellectualPropertyWarning) ? "" : $" ({enrichment.IntellectualPropertyWarning})"));

        var ready = missing.Count == 0;
        product.Status = ready ? "ReadyToPublish" : "NeedsReview";
        product.UpdatedAt = DateTime.UtcNow;
        publication.UpdatedAt = DateTime.UtcNow;
        publication.AutopilotNotes = Truncate(ready ? "Listo para publicar." : "Falta: " + string.Join("; ", missing), 1000);

        await _context.SaveChangesAsync(cancellationToken);

        // 5b) Foto de portada con fondo blanco (si está prendido en Automático y aún no se publica).
        if (string.IsNullOrWhiteSpace(publication.ExternalItemId) && !string.IsNullOrWhiteSpace(product.ImageUrl) && product.OriginalImageUrl is null)
        {
            var photos = _services.GetService<MarketplaceSync.Services.Photos.PhotoStudioService>();
            var fixPhotos = await _context.AutomationSettings.AsNoTracking()
                .Where(a => a.OrganizationId == organizationId).Select(a => (bool?)a.FixPhotosOnPublish).FirstOrDefaultAsync(cancellationToken) ?? true;
            if (photos is { ToolAvailable: true } && fixPhotos && accountId.HasValue)
            {
                try
                {
                    var photo = await photos.FixAsync(organizationId, product.Id, updateListings: false, cancellationToken);
                    if (photo.Error is null && product.OriginalImageUrl is not null)
                        done.Add("Foto con fondo blanco, 1200×1200");
                    else if (photo.Error is not null)
                        _logger.LogInformation("Autopilot photo skipped for {Product}: {Error}", product.Id, photo.Error);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(exception, "Autopilot photo fix failed for {Product}.", product.Id);
                }
            }
        }

        // 6) Publicar
        var published = false;
        string? permalink = publication.Permalink;
        if (publishIfReady && ready)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                missing.Add("Conecta una cuenta de Mercado Libre para publicar");
            }
            else if (hasVariations)
            {
                missing.Add("Tiene variantes: publícalo desde el formulario para elegir las combinaciones");
            }
            else
            {
                // Solo un tipo de publicación que la cuenta pueda usar en esta categoría.
                if (string.IsNullOrWhiteSpace(publication.ExternalItemId) && accountId is not null)
                {
                    var sellerId = await _context.MarketplaceAccounts
                        .Where(x => x.Id == accountId && x.OrganizationId == organizationId)
                        .Select(x => x.ExternalAccountId)
                        .FirstOrDefaultAsync(cancellationToken);
                    var types = string.IsNullOrWhiteSpace(sellerId)
                        ? null
                        : await _listingClient.GetAvailableListingTypesAsync(accessToken, sellerId, publication.CategoryId!, cancellationToken);
                    if (types is { Count: > 0 } && types.All(t => t.Id != (publication.ListingTypeId ?? MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DefaultListingTypeId)))
                    {
                        done.Add($"Tipo de publicación: {types[0].Name} (el elegido no está disponible en esta categoría)");
                        publication.ListingTypeId = types[0].Id;
                    }
                }

                var attributes = chosen.ToList();
                var sku = activeVariants.FirstOrDefault()?.Sku;
                if (!string.IsNullOrWhiteSpace(sku) && attributes.All(a => a.Id != "SELLER_SKU"))
                    attributes.Add(new SavedAttributeValue("SELLER_SKU", null, sku));

                Task<ListingPublishResult> PublishAsync() => _listingClient.PublishSimpleItemAsync(
                    accessToken,
                    publication.ExternalItemId,
                    product.Title!.Length > 60 ? product.Title[..60] : product.Title,
                    publication.CategoryId!,
                    publication.Price!.Value,
                    publication.Stock!.Value,
                    publication.Condition ?? "new",
                    publication.ListingTypeId ?? MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DefaultListingTypeId,
                    product.ImageUrl,
                    attributes,
                    product.Description,
                    cancellationToken);

                var result = await PublishAsync();

                // Mercado Libre pidió atributos que no venían en el esquema: se llenan y se reintenta una vez.
                if (!result.Success && result.RawError is not null)
                {
                    var added = await FillRequestedAttributesAsync(product, publication.CategoryId!, accessToken, result.RawError, attributes, cancellationToken);
                    if (added.Count > 0)
                    {
                        done.Add("Mercado Libre pidió más datos y los completé: " + string.Join(", ", added));
                        result = await PublishAsync();
                    }
                }

                if (result.Success)
                {
                    published = true;
                    permalink = result.Permalink ?? permalink;
                    publication.ExternalItemId = result.ItemId;
                    publication.Permalink = permalink;
                    publication.Status = result.Status ?? "active";
                    publication.IsPublished = true;
                    publication.PublishedAt ??= DateTime.UtcNow;
                    publication.AutopilotNotes = "Publicado por el piloto automático.";
                    publication.AttributesJson = JsonSerializer.Serialize(attributes.Where(a => a.Id != "SELLER_SKU"));
                    product.LastErrorMessage = null;
                    done.Add("Publicado en Mercado Libre");
                }
                else
                {
                    missing.Add("Mercado Libre rechazó la publicación: " + result.ErrorMessage);
                    publication.AutopilotNotes = Truncate("Mercado Libre rechazó la publicación: " + result.ErrorMessage, 1000);
                    product.LastErrorMessage = Truncate(result.ErrorMessage ?? "Error al publicar", 1000);
                    product.LastErrorAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        return new AutopilotResult(product.Id, ready, published, permalink, done, missing, _claude.IsConfigured, enrichment.ProcessingEngine);
    }

    /// <summary>Atributos guardados en la publicación (para reutilizarlos o precargar el formulario).</summary>
    public static List<SavedAttributeValue> ReadSavedAttributes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<SavedAttributeValue>();

        try
        {
            return JsonSerializer.Deserialize<List<SavedAttributeValue>>(json) ?? new List<SavedAttributeValue>();
        }
        catch (JsonException)
        {
            return new List<SavedAttributeValue>();
        }
    }

    /// <summary>Elige la opción oficial si existe (value_id); si no, usa el texto como value_name.</summary>
    /// <summary>
    /// Busca el código en la base pública de códigos y lo usa solo si coincide marca, tamaño y piezas.
    /// Se busca una vez por producto.
    /// </summary>
    private async Task<string?> FindBarcodeOnlineAsync(Product product, CancellationToken cancellationToken)
    {
        const string noMatch = "internet: sin coincidencia";
        var lookup = _services.GetService<MarketplaceSync.Services.Products.BarcodeLookupService>();
        var fill = _services.GetService<MarketplaceSync.Services.Products.AttributeAutoFillService>();
        if (lookup is null || fill is null || product.SourceGtinOrigin == noMatch || !string.IsNullOrWhiteSpace(product.SourceGtin))
            return product.SourceGtin;

        var facts = new MarketplaceSync.Services.Products.ProductFacts(product.Title, product.Description, product.Brand, product.Model, product.SourceUrl);
        var query = (await fill.FillAsync(facts, new List<MercadoLibreAttributeInput>(), true, null, cancellationToken)).BarcodeQuery;
        var best = (await lookup.SearchAsync(query, product.Brand, cancellationToken)).FirstOrDefault(c => c.Strong);
        if (best is null)
        {
            product.SourceGtinOrigin = noMatch;
            return null;
        }

        product.SourceGtin = best.Gtin;
        product.SourceGtinOrigin = MarketplaceSync.Services.Products.BarcodeLookupService.OriginName;
        product.SourceGtinCheckedAt = DateTime.UtcNow;
        return best.Gtin;
    }

    /// <summary>
    /// Lee qué atributos reclamó Mercado Libre, los pide al esquema, los llena (IA o código real) y los agrega.
    /// Regresa "Nombre = valor" de lo agregado; vacío si no se pudo llenar todo lo que pidió.
    /// </summary>
    private async Task<List<string>> FillRequestedAttributesAsync(
        Product product,
        string categoryId,
        string accessToken,
        string rawError,
        List<SavedAttributeValue> attributes,
        CancellationToken cancellationToken)
    {
        var added = new List<string>();
        var requested = MercadoLibreListingClient.ParseMissingAttributes(rawError);
        if (!requested.Any)
            return added;

        List<MercadoLibreAttributeInput> schema;
        try
        {
            schema = await _listingClient.GetRequiredAttributesAsync(categoryId, accessToken, cancellationToken, requested.All);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Autopilot could not reload attributes for {CategoryId}.", categoryId);
            return added;
        }

        var names = requested.Names.Select(MercadoLibreListingClient.NormalizeName).ToHashSet();
        var asked = schema
            .Where(a => requested.Ids.Contains(a.Id, StringComparer.OrdinalIgnoreCase) || names.Contains(MercadoLibreListingClient.NormalizeName(a.Name)))
            .Where(a => attributes.All(x => !x.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (asked.Count == 0)
            return added;

        var gtin = asked.FirstOrDefault(a => a.Id == MercadoLibreListingClient.GtinId);
        if (gtin is not null)
        {
            var code = await FindBarcodeOnlineAsync(product, cancellationToken);
            if (code is null)
                return added; // sin código real no se inventa: queda para que el vendedor lo escanee
            gtin.ValueName = code;
        }

        var fill = _services.GetService<MarketplaceSync.Services.Products.AttributeAutoFillService>();
        if (fill is not null)
        {
            var facts = new MarketplaceSync.Services.Products.ProductFacts(product.Title, product.Description, product.Brand, product.Model, product.SourceUrl);
            await fill.FillAsync(facts, asked, false, null, cancellationToken);
        }

        var unresolved = asked.Where(a => a.Id != MercadoLibreListingClient.EmptyGtinReasonId &&
                                          string.IsNullOrWhiteSpace(a.ValueId) && string.IsNullOrWhiteSpace(a.ValueName)).ToList();
        if (unresolved.Count > 0)
            return added;

        foreach (var attribute in asked.Where(a => !string.IsNullOrWhiteSpace(a.ValueId) || !string.IsNullOrWhiteSpace(a.ValueName)))
        {
            var isName = attribute.ValueId?.StartsWith("name:", StringComparison.Ordinal) == true;
            attributes.Add(new SavedAttributeValue(attribute.Id, isName ? null : attribute.ValueId, isName ? attribute.ValueId![5..] : attribute.ValueName));
            added.Add($"{(attribute.Id == MercadoLibreListingClient.GtinId ? "código de barras" : attribute.Name)} = {attribute.ValueName}");
        }
        return added;
    }

    private static SavedAttributeValue? ResolveAttribute(MercadoLibreAttributeInput attribute, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (attribute.Values.Count > 0)
        {
            var option = attribute.Values.FirstOrDefault(o => string.Equals(o.Name, value, StringComparison.OrdinalIgnoreCase))
                         ?? attribute.Values.FirstOrDefault(o =>
                             o.Name.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                             value.Contains(o.Name, StringComparison.OrdinalIgnoreCase));
            if (option is not null)
                return new SavedAttributeValue(attribute.Id, string.IsNullOrWhiteSpace(option.Id) ? null : option.Id, option.Name);
        }

        // Valores numéricos o de texto libre ("No aplica" no se acepta como dato real).
        if (value.Equals("No aplica", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Estándar", StringComparison.OrdinalIgnoreCase))
            return null;

        return new SavedAttributeValue(attribute.Id, null, value.Length > 255 ? value[..255] : value);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
