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
    private readonly ILogger<ListingAutopilotService> _logger;

    public ListingAutopilotService(
        AppDbContext context,
        MercadoLibreCategoryService categoryService,
        MercadoLibreListingClient listingClient,
        IProductAiEnricherService enricher,
        ICrossBorderPricingService pricing,
        IMercadoLibreAuthService authService,
        ClaudeClient claude,
        ILogger<ListingAutopilotService> logger)
    {
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
            ListingTypeId = "gold_special",
            Condition = "new",
            Status = "Draft",
            IsPublished = false,
            CreatedAt = DateTime.UtcNow
        };
        if (existing is null)
            _context.MarketplacePublications.Add(publication);
        publication.MarketplaceAccountId ??= accountId;

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
            RequiredAttributes: schema
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
        foreach (var attribute in schema)
        {
            var value = enrichment.Attributes.GetValueOrDefault(attribute.Id)
                        ?? (attribute.Id.Equals("BRAND", StringComparison.OrdinalIgnoreCase) ? product.Brand : null)
                        ?? (attribute.Id is "MODEL" or "PART_NUMBER" or "MPN" ? product.Model : null);

            var resolved = ResolveAttribute(attribute, value)
                           ?? saved.FirstOrDefault(x => x.Id.Equals(attribute.Id, StringComparison.OrdinalIgnoreCase));
            if (resolved is null)
                missing.Add($"Atributo obligatorio: {attribute.Name}");
            else
                chosen.Add(resolved);
        }
        if (schema.Count > 0 && chosen.Count == schema.Count)
            done.Add($"{chosen.Count} atributos obligatorios completos");
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
                var attributes = chosen.ToList();
                var sku = activeVariants.FirstOrDefault()?.Sku;
                if (!string.IsNullOrWhiteSpace(sku) && attributes.All(a => a.Id != "SELLER_SKU"))
                    attributes.Add(new SavedAttributeValue("SELLER_SKU", null, sku));

                var result = await _listingClient.PublishSimpleItemAsync(
                    accessToken,
                    publication.ExternalItemId,
                    product.Title!.Length > 60 ? product.Title[..60] : product.Title,
                    publication.CategoryId!,
                    publication.Price!.Value,
                    publication.Stock!.Value,
                    publication.Condition ?? "new",
                    publication.ListingTypeId ?? "gold_special",
                    product.ImageUrl,
                    attributes,
                    product.Description,
                    cancellationToken);

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
