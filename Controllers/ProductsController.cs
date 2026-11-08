using MarketplaceSync.Web.ViewModels.Products;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceSync.Web.Services;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.ViewModels;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using MarketplaceSync.Web.Services.Tenancy;
using MarketplaceSync.Services.Interfaces;
namespace MarketplaceSync.Web.Controllers
{
    [Authorize]
    public class ProductsController : Controller
{
        private async Task PopulateMarketplaceAccountsAsync(
            PublishToMercadoLibreRequest request,
            Guid organizationId)
        {
            request.Accounts = await _context.MarketplaceAccounts
                .Where(x => x.OrganizationId == organizationId &&
                            x.Marketplace == "MercadoLibre" &&
                            x.Status == "Connected" &&
                            x.Tokens.Any(token => token.IsActive))
                .OrderBy(x => x.DisplayName)
                .Select(x => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem
                {
                    Value = x.Id.ToString(),
                    Text = string.IsNullOrWhiteSpace(x.DisplayName)
                        ? x.ExternalAccountId
                        : $"{x.DisplayName} ({x.ExternalAccountId})"
                })
                .ToListAsync();

            if (request.MarketplaceAccountId.HasValue &&
                request.Accounts.All(x => x.Value != request.MarketplaceAccountId.Value.ToString()))
            {
                request.MarketplaceAccountId = null;
            }

            if (!request.MarketplaceAccountId.HasValue && request.Accounts.Count > 0)
                request.MarketplaceAccountId = int.Parse(request.Accounts[0].Value!);

            await LoadListingTypesAsync(request, organizationId);
        }

        /// <summary>
        /// Deja en el formulario solo los tipos de publicación que la cuenta puede usar en la categoría
        /// y, si el elegido no está, selecciona el primero disponible (Clásica antes que Premium).
        /// </summary>
        private async Task LoadListingTypesAsync(PublishToMercadoLibreRequest request, Guid organizationId)
        {
            if (!request.MarketplaceAccountId.HasValue ||
                string.IsNullOrWhiteSpace(request.CategoryId) ||
                !Regex.IsMatch(request.CategoryId.Trim(), @"^MLM\d+$", RegexOptions.IgnoreCase))
                return;

            var sellerId = await _context.MarketplaceAccounts
                .Where(x => x.Id == request.MarketplaceAccountId.Value && x.OrganizationId == organizationId)
                .Select(x => x.ExternalAccountId)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(sellerId))
                return;

            string accessToken;
            try
            {
                accessToken = await _mercadoLibreAuthService.GetValidAccessTokenAsync(organizationId, request.MarketplaceAccountId.Value);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "No token to read listing types for account {AccountId}.", request.MarketplaceAccountId);
                return;
            }

            var options = await _listingClient.GetAvailableListingTypesAsync(
                accessToken, sellerId, request.CategoryId.Trim().ToUpperInvariant());
            if (options is null)
                return;

            request.ListingTypeOptions = options;
            if (options.Count == 0)
            {
                request.ListingTypeNote = "Mercado Libre no ofrece ningún tipo de publicación para tu cuenta en esta categoría. " +
                                          "Puede ser una restricción de la cuenta o de la categoría: revisa los avisos en tu cuenta de Mercado Libre o prueba otra categoría.";
                return;
            }

            if (options.All(x => x.Id != request.ListingTypeId))
            {
                var previous = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.ListingTypeName(request.ListingTypeId);
                request.ListingTypeId = options[0].Id;
                ModelState.Remove(nameof(request.ListingTypeId));
                request.ListingTypeNote = $"{previous} no está disponible para tu cuenta en esta categoría; dejé seleccionada {options[0].Name}.";
            }
        }

        private async Task<MarketplacePublication> GetOrCreateMarketplacePublicationAsync(
            Product product,
            int? marketplaceAccountId = null)
        {
            var publication = await _context.MarketplacePublications
                .FirstOrDefaultAsync(x =>
                    x.ProductId == product.Id &&
                    x.Marketplace == "MercadoLibre" &&
                    x.MarketplaceAccountId == marketplaceAccountId);

            if (publication == null && marketplaceAccountId.HasValue)
            {
                publication = await _context.MarketplacePublications
                    .FirstOrDefaultAsync(x =>
                        x.ProductId == product.Id &&
                        x.Marketplace == "MercadoLibre" &&
                        x.MarketplaceAccountId == null);
                if (publication != null)
                    publication.MarketplaceAccountId = marketplaceAccountId;
            }

            if (publication != null)
                return publication;

            publication = new MarketplacePublication
            {
                Product = product,
                Marketplace = "MercadoLibre",
                MarketplaceAccountId = marketplaceAccountId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.MarketplacePublications.Add(publication);
            return publication;
        }

          private readonly AppDbContext _context;

    private readonly MarketplaceDetectorService _detector;
private readonly ProductExtractorService _extractor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MercadoLibreCategoryService _mercadoLibreCategoryService;
    private readonly IOrganizationContext _organizationContext;
    private readonly ILogger<ProductsController> _logger;
    private readonly IMercadoLibreAuthService _mercadoLibreAuthService;
    private readonly IMercadoLibreFeeService _mercadoLibreFeeService;
    private readonly ICrossBorderPricingService _crossBorderPricingService;
    private readonly IProductAiEnricherService _aiEnricher;
    private readonly MarketplaceSync.Services.Ai.IListingAutopilotService _autopilot;
    private readonly MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient _listingClient;
    private readonly MarketplaceSync.Services.Ai.ProductTitleTranslator _titleTranslator;

public ProductsController(
    AppDbContext context,
    MarketplaceDetectorService detector,
    ProductExtractorService extractor,
    IHttpClientFactory httpClientFactory,
    MercadoLibreCategoryService mercadoLibreCategoryService,
    IOrganizationContext organizationContext,
    IMercadoLibreAuthService mercadoLibreAuthService,
    IMercadoLibreFeeService mercadoLibreFeeService,
    ICrossBorderPricingService crossBorderPricingService,
    IProductAiEnricherService aiEnricher,
    MarketplaceSync.Services.Ai.IListingAutopilotService autopilot,
    MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient listingClient,
    MarketplaceSync.Services.Ai.ProductTitleTranslator titleTranslator,
    MarketplaceSync.Services.Products.SourceBarcodeService sourceBarcodes,
    MarketplaceSync.Services.Products.AttributeAutoFillService autoFill,
    MarketplaceSync.Services.Products.BarcodeLookupService barcodeLookup,
    ILogger<ProductsController> logger
)
{
    _titleTranslator = titleTranslator;
    _context = context;
    _detector = detector;
    _extractor = extractor;
    _httpClientFactory = httpClientFactory;
    _mercadoLibreCategoryService = mercadoLibreCategoryService;
    _organizationContext = organizationContext;
    _mercadoLibreAuthService = mercadoLibreAuthService;
    _mercadoLibreFeeService = mercadoLibreFeeService;
    _crossBorderPricingService = crossBorderPricingService;
    _aiEnricher = aiEnricher;
    _autopilot = autopilot;
    _listingClient = listingClient;
    _logger = logger;
    _sourceBarcodes = sourceBarcodes;
    _autoFill = autoFill;
    _barcodeLookup = barcodeLookup;
}

private readonly MarketplaceSync.Services.Products.AttributeAutoFillService _autoFill;
private readonly MarketplaceSync.Services.Products.BarcodeLookupService _barcodeLookup;

private readonly MarketplaceSync.Services.Products.SourceBarcodeService _sourceBarcodes;
        
        private async Task<List<MercadoLibreAttributeInput>> GetRequiredAttributesAsync(
            string categoryId,
            int? marketplaceAccountId,
            IEnumerable<string>? alsoInclude = null)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var token = await _context.MercadoLibreTokens
        .Where(x => x.OrganizationId == organizationId &&
                    x.MarketplaceAccountId == marketplaceAccountId &&
                    x.IsActive)
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefaultAsync();

    return await _listingClient.GetRequiredAttributesAsync(categoryId, token?.AccessToken, alsoInclude: alsoInclude);
}

/// <summary>
/// Completa solo lo que se puede: atributos que la IA deduce con seguridad y el código de barras real
/// (variante → anuncio de origen → base pública de códigos, solo si coincide). <paramref name="onlyIds"/> limita los atributos.
/// </summary>
private async Task AutoCompleteAsync(
    PublishToMercadoLibreRequest request,
    Product product,
    Guid organizationId,
    IReadOnlyCollection<string>? onlyIds = null)
{
    var needsBarcode = await PrefillGtinAsync(request, product, organizationId);
    var facts = new MarketplaceSync.Services.Products.ProductFacts(
        string.IsNullOrWhiteSpace(request.Title) ? product.Title : request.Title,
        product.Description,
        string.IsNullOrWhiteSpace(request.Brand) ? product.Brand : request.Brand,
        string.IsNullOrWhiteSpace(request.Model) ? product.Model : request.Model,
        product.SourceUrl);
    var fill = await _autoFill.FillAsync(facts, request.Attributes, needsBarcode, onlyIds);
    if (needsBarcode)
        await PrefillGtinFromInternetAsync(request, product, facts.Brand, fill.BarcodeQuery);
}

/// <summary>
/// Prellena el GTIN con un código real: el de una variante o el del anuncio de origen (eBay/Walmart, o uno
/// encontrado antes en internet). Nunca se genera uno. Regresa true si el campo existe y sigue vacío.
/// </summary>
private async Task<bool> PrefillGtinAsync(PublishToMercadoLibreRequest request, Product product, Guid organizationId)
{
    var gtin = request.Attributes.FirstOrDefault(a => a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId);
    if (gtin is null || !string.IsNullOrWhiteSpace(gtin.ValueName) || !string.IsNullOrWhiteSpace(gtin.ValueId))
        return false;

    var barcodes = await _context.ProductVariants
        .AsNoTracking()
        .Where(v => v.ProductId == product.Id && v.OrganizationId == organizationId && v.IsActive && v.Barcode != null)
        .Select(v => v.Barcode!)
        .ToListAsync();
    foreach (var barcode in barcodes)
    {
        if (MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.ValidateGtin(barcode, out var digits) is null)
        {
            gtin.ValueName = digits;
            request.GtinSource = "Tomado del código de barras de tu inventario.";
            return false;
        }
    }

    var checkedBefore = product.SourceGtinCheckedAt;
    var fromSource = await _sourceBarcodes.EnsureAsync(product);
    if (product.SourceGtinCheckedAt != checkedBefore)
        await _context.SaveChangesAsync();
    if (fromSource is null)
        return true;

    gtin.ValueName = fromSource;
    request.GtinSource = product.SourceGtinOrigin == MarketplaceSync.Services.Products.BarcodeLookupService.OriginName
        ? "Encontrado en internet (UPCitemdb). Revisa que coincida con la etiqueta antes de publicar."
        : $"Tomado del anuncio de {(product.SourceGtinOrigin ?? (string.IsNullOrWhiteSpace(product.SourceMarketplace) ? "origen" : product.SourceMarketplace))}. Revisa que coincida con la etiqueta.";
    return false;
}

/// <summary>
/// Busca el código en la base pública de códigos. Solo lo pone si coincide marca, tamaño y piezas;
/// si no, deja los candidatos para que el vendedor elija. Se busca una vez por producto.
/// </summary>
private async Task PrefillGtinFromInternetAsync(PublishToMercadoLibreRequest request, Product product, string? brand, string? query)
{
    const string noMatch = "internet: sin coincidencia";
    if (product.SourceGtinOrigin == noMatch || string.IsNullOrWhiteSpace(query))
        return;
    var gtin = request.Attributes.FirstOrDefault(a => a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId);
    if (gtin is null)
        return;

    var candidates = await _barcodeLookup.SearchAsync(query, brand);
    var best = candidates.FirstOrDefault(c => c.Strong);
    if (best is not null)
    {
        gtin.ValueName = best.Gtin;
        product.SourceGtin = best.Gtin;
        product.SourceGtinOrigin = MarketplaceSync.Services.Products.BarcodeLookupService.OriginName;
        product.SourceGtinCheckedAt = DateTime.UtcNow;
        request.GtinSource = $"Encontrado en internet (UPCitemdb): “{best.Title}”. Revisa que coincida con la etiqueta antes de publicar.";
    }
    else
    {
        request.GtinCandidates = candidates;
        if (candidates.Count == 0)
            product.SourceGtinOrigin = noMatch;
    }
    await _context.SaveChangesAsync();
}

private static void ApplySavedAttributes(PublishToMercadoLibreRequest request, string? attributesJson)
{
    var saved = MarketplaceSync.Services.Ai.ListingAutopilotService.ReadSavedAttributes(attributesJson);
    foreach (var attribute in request.Attributes)
    {
        var value = saved.FirstOrDefault(x => x.Id.Equals(attribute.Id, StringComparison.OrdinalIgnoreCase));
        if (value is null)
            continue;

        attribute.ValueName = value.ValueName ?? attribute.ValueName;
        attribute.ValueId = !string.IsNullOrWhiteSpace(value.ValueId)
            ? value.ValueId
            : attribute.Values.Any(o => string.Equals(o.Name, value.ValueName, StringComparison.OrdinalIgnoreCase))
                ? $"name:{value.ValueName}"
                : attribute.ValueId;
    }
}

private static void PrefillKnownAttributes(PublishToMercadoLibreRequest request)
{
    foreach (var attribute in request.Attributes)
    {
        if (attribute.Id.Equals("BRAND", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(request.Brand))
            PrefillAttribute(attribute, request.Brand);

        if (attribute.Id.Equals("MODEL", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(request.Model))
            PrefillAttribute(attribute, request.Model);
    }
}

private static void PrefillAttribute(MercadoLibreAttributeInput attribute, string value)
{
    var matchingOption = attribute.Values.FirstOrDefault(option =>
        string.Equals(option.Name, value, StringComparison.OrdinalIgnoreCase));
    attribute.ValueName = matchingOption?.Name ?? value;
    if (matchingOption is not null)
    {
        attribute.ValueId = string.IsNullOrWhiteSpace(matchingOption.Id)
            ? $"name:{matchingOption.Name}"
            : matchingOption.Id;
    }
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> LoadMercadoLibreAttributes(PublishToMercadoLibreRequest request)
{
    ModelState.Clear();

    if (string.IsNullOrWhiteSpace(request.CategoryId))
    {
        ModelState.AddModelError(nameof(request.CategoryId), "Primero ingresa una categoría de Mercado Libre.");
        return View("PublishToMercadoLibre", request);
    }

    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    await PopulateMarketplaceAccountsAsync(request, organizationId);
    if (!Regex.IsMatch(request.CategoryId.Trim(), @"^MLM\d+$", RegexOptions.IgnoreCase))
    {
        ModelState.AddModelError(nameof(request.CategoryId), "Usa una categoría válida de México, por ejemplo MLM12345.");
        return View("PublishToMercadoLibre", request);
    }

    try
    {
        request.CategoryId = request.CategoryId.Trim().ToUpperInvariant();
        request.Attributes = await GetRequiredAttributesAsync(request.CategoryId, request.MarketplaceAccountId);
        request.AttributesLoaded = true;
        request.AttributeLoadError = null;
        PrefillKnownAttributes(request);
        var productForGtin = await _context.Products
            .FirstOrDefaultAsync(x => x.Id == request.ProductId && x.OrganizationId == organizationId);
        if (productForGtin is not null)
            await AutoCompleteAsync(request, productForGtin, organizationId);
        if (!request.Attributes.Any())
            TempData["Success"] = "La categoría es válida y no reportó atributos obligatorios adicionales.";
    }
    catch (Exception exception)
    {
        _logger.LogWarning(exception, "Could not load Mercado Libre attributes for category {CategoryId}.", request.CategoryId);
        ModelState.AddModelError(nameof(request.CategoryId), "No fue posible consultar esa categoría en Mercado Libre. Verifica el ID e inténtalo de nuevo.");
    }

    return View("PublishToMercadoLibre", request);
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SuggestMercadoLibreCategories(PublishToMercadoLibreRequest request)
{
    ModelState.Clear();
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    await PopulateMarketplaceAccountsAsync(request, organizationId);

    if (string.IsNullOrWhiteSpace(request.Title))
    {
        ModelState.AddModelError(nameof(request.Title), "Escribe un título antes de solicitar categorías.");
        return View("PublishToMercadoLibre", request);
    }

    try
    {
        request.CategorySuggestions = await _mercadoLibreCategoryService
            .SuggestCategoriesAsync(request.Title);

        if (!request.CategorySuggestions.Any())
            ModelState.AddModelError(nameof(request.CategoryId), "Mercado Libre no encontró categorías para este título.");
    }
    catch (Exception exception)
    {
        _logger.LogWarning(exception, "Could not suggest Mercado Libre categories for product {ProductId}.", request.ProductId);
        ModelState.AddModelError(nameof(request.CategoryId), "No fue posible obtener sugerencias de Mercado Libre. Inténtalo nuevamente.");
    }

    return View("PublishToMercadoLibre", request);
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SaveMercadoLibreCategory(int id, string mercadoLibreCategoryId)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
        return NotFound();

    if (string.IsNullOrWhiteSpace(mercadoLibreCategoryId))
    {
        TempData["Error"] = "Selecciona una categoría válida.";
        return RedirectToAction(nameof(PublishToMercadoLibre), new { id });
    }

    var publication = await GetOrCreateMarketplacePublicationAsync(product);
    publication.CategoryId = mercadoLibreCategoryId.Trim();
    publication.UpdatedAt = DateTime.UtcNow;
    product.UpdatedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync();

    TempData["Success"] = $"Categoría Mercado Libre guardada: {publication.CategoryId}";

    return RedirectToAction(nameof(PublishToMercadoLibre), new { id });
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SuggestMercadoLibreCategory(int id)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
        return NotFound();

    if (string.IsNullOrWhiteSpace(product.Title))
    {
        TempData["Error"] = "El producto no tiene título para sugerir categoría.";
        return RedirectToAction(nameof(PublishToMercadoLibre), new { id });
    }

    var suggestions = await _mercadoLibreCategoryService.SuggestCategoriesAsync(product.Title);

    if (!suggestions.Any())
    {
        TempData["Error"] = "Mercado Libre no regresó categorías sugeridas.";
        return RedirectToAction(nameof(PublishToMercadoLibre), new { id });
    }

    TempData["CategorySuggestions"] = JsonSerializer.Serialize(suggestions);

    return RedirectToAction(nameof(PublishToMercadoLibre), new { id });
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> RefreshProduct(int id)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
    {
        TempData["Error"] = "Producto no encontrado.";
        return RedirectToAction(nameof(Index));
    }

    if (string.IsNullOrWhiteSpace(product.SourceUrl))
    {
        TempData["Error"] = "El producto no tiene URL de origen para actualizar.";
        return RedirectToAction(nameof(Index));
    }

    try
    {
        var extracted = await _extractor.ExtractAsync(product.SourceUrl.Trim());

        product.SourceMarketplace = extracted.SourceMarketplace ?? product.SourceMarketplace;
        product.SourceProductId = extracted.SourceProductId ?? product.SourceProductId;
        if (extracted.Gtin is not null) { product.SourceGtin = extracted.Gtin; product.SourceGtinOrigin = extracted.SourceMarketplace; product.SourceGtinCheckedAt = DateTime.UtcNow; }

        // No pisa títulos editados o generados con IA: solo se completa si está vacío.
        if (string.IsNullOrWhiteSpace(product.Title)) product.Title = extracted.Title;
        if (string.IsNullOrWhiteSpace(product.Description)) product.Description = extracted.Description;

        product.SourcePrice = extracted.SourcePrice ?? product.SourcePrice;
        product.SourceCurrency = extracted.SourceCurrency ?? product.SourceCurrency;
        product.SourceStock = extracted.SourceStock ?? product.SourceStock;

        product.ImageUrl = extracted.ImageUrl ?? product.ImageUrl;
        product.Brand = extracted.Brand ?? product.Brand;
        product.Model = extracted.Model ?? product.Model;

        product.SourceStatus = extracted.SourceStatus ?? "Updated";
        product.LastSourceCheckAt = DateTime.UtcNow;
        product.UpdatedAt = DateTime.UtcNow;

        product.Status = "Updated";

        await _context.SaveChangesAsync();

        TempData["Success"] = "Información del producto actualizada correctamente.";
    }
    catch (Exception ex)
    {
        TempData["Error"] = $"Error al actualizar producto: {ex.Message}";
    }

    return RedirectToAction(nameof(Index));
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> RefreshSource(int id)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
    {
        TempData["Error"] = "Producto no encontrado.";
        return RedirectToAction(nameof(Index));
    }

    if (string.IsNullOrWhiteSpace(product.SourceUrl))
    {
        TempData["Error"] = "El producto no tiene URL de origen.";
        return RedirectToAction(nameof(Index));
    }

    try
    {
        var extracted = await _extractor.ExtractAsync(product.SourceUrl.Trim());

        product.SourceMarketplace = extracted.SourceMarketplace ?? product.SourceMarketplace;
        product.SourceProductId = extracted.SourceProductId ?? product.SourceProductId;
        if (extracted.Gtin is not null) { product.SourceGtin = extracted.Gtin; product.SourceGtinOrigin = extracted.SourceMarketplace; product.SourceGtinCheckedAt = DateTime.UtcNow; }

        // No pisa títulos editados o generados con IA: solo se completa si está vacío.
        if (string.IsNullOrWhiteSpace(product.Title)) product.Title = extracted.Title;
        if (string.IsNullOrWhiteSpace(product.Description)) product.Description = extracted.Description;
        product.SourcePrice = extracted.SourcePrice ?? product.SourcePrice;
        product.SourceCurrency = extracted.SourceCurrency ?? product.SourceCurrency;
        product.SourceStock = extracted.SourceStock ?? product.SourceStock;
        product.SourceAvailabilityText = extracted.SourceAvailabilityText ?? product.SourceAvailabilityText;

        product.ImageUrl = extracted.ImageUrl ?? product.ImageUrl;
        product.Brand = extracted.Brand ?? product.Brand;
        product.Model = extracted.Model ?? product.Model;

        product.SourceStatus = extracted.SourceStatus ?? "Updated";
        product.LastSourceCheckAt = DateTime.UtcNow;
        product.UpdatedAt = DateTime.UtcNow;

        product.LastErrorAt = null;
        product.LastErrorMessage = null;

        if (product.SourceStock.HasValue && product.SourceStock.Value <= 0)
            product.Status = "OutOfStock";
        else
            product.Status = "Updated";

        await _context.SaveChangesAsync();

        TempData["Success"] = "Producto actualizado correctamente.";
    }
    catch (Exception ex)
    {
        product.LastErrorAt = DateTime.UtcNow;
        product.LastErrorMessage = ex.Message.Length > 2000
            ? ex.Message.Substring(0, 2000)
            : ex.Message;

        product.SourceStatus = "RefreshError";
        product.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        TempData["Error"] = $"Error al actualizar producto: {ex.Message}";
    }

    return RedirectToAction(nameof(Index));
}// GET: /Products/Delete/5
[HttpGet]
public async Task<IActionResult> Delete(int id)
{
  var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

var product = await _context.Products
    .Include(x => x.MarketplacePublications)
    .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
    {
        return NotFound();
    }

    return View(product);
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> RefreshAllSourceInfo()
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var products = await _context.Products
        .Where(x => x.OrganizationId == organizationId && !string.IsNullOrWhiteSpace(x.SourceUrl))
        .OrderByDescending(x => x.CreatedAt)
        .ToListAsync();

    if (!products.Any())
    {
        TempData["Error"] = "No hay productos con URL de origen para actualizar.";
        return RedirectToAction(nameof(Index));
    }

    var updated = 0;
    var failed = 0;

    foreach (var product in products)
    {
        try
        {
            var extracted = await _extractor.ExtractAsync(product.SourceUrl.Trim());

            product.SourceMarketplace = extracted.SourceMarketplace ?? product.SourceMarketplace;
            product.SourceProductId = extracted.SourceProductId ?? product.SourceProductId;
            if (extracted.Gtin is not null) { product.SourceGtin = extracted.Gtin; product.SourceGtinOrigin = extracted.SourceMarketplace; product.SourceGtinCheckedAt = DateTime.UtcNow; }

            // No pisa títulos editados o generados con IA: solo se completa si está vacío.
        if (string.IsNullOrWhiteSpace(product.Title)) product.Title = extracted.Title;
            if (string.IsNullOrWhiteSpace(product.Description)) product.Description = extracted.Description;
            product.SourcePrice = extracted.SourcePrice ?? product.SourcePrice;
            product.SourceCurrency = extracted.SourceCurrency ?? product.SourceCurrency;
            product.SourceStock = extracted.SourceStock ?? product.SourceStock;
            product.SourceAvailabilityText = extracted.SourceAvailabilityText ?? product.SourceAvailabilityText;

            product.ImageUrl = extracted.ImageUrl ?? product.ImageUrl;
            product.Brand = extracted.Brand ?? product.Brand;
            product.Model = extracted.Model ?? product.Model;

            product.SourceStatus = extracted.SourceStatus ?? "Updated";
            product.LastSourceCheckAt = DateTime.UtcNow;
            product.UpdatedAt = DateTime.UtcNow;

            product.LastErrorAt = null;
            product.LastErrorMessage = null;

            if (product.SourceStock.HasValue && product.SourceStock.Value <= 0)
                product.Status = "OutOfStock";
            else
                product.Status = "Updated";

            updated++;
        }
        catch (Exception ex)
        {
            product.LastErrorAt = DateTime.UtcNow;
            product.LastErrorMessage = ex.Message.Length > 2000
                ? ex.Message.Substring(0, 2000)
                : ex.Message;

            product.SourceStatus = "RefreshError";
            product.UpdatedAt = DateTime.UtcNow;

            failed++;
        }
    }

    await _context.SaveChangesAsync();

    TempData["Success"] = $"Actualización finalizada. Actualizados: {updated}. Fallidos: {failed}.";

    return RedirectToAction(nameof(Index));
}
// POST: /Products/Delete/5
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteConfirmed(int id)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == organizationId);

    if (product == null)
    {
        return NotFound();
    }

    _context.Products.Remove(product);
    await _context.SaveChangesAsync();

    TempData["Success"] = "Producto eliminado correctamente.";

    return RedirectToAction(nameof(Index));
}
[HttpGet]
public async Task<IActionResult> Details(int id)
{var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

var product = await _context.Products
    .Include(x => x.MarketplacePublications)
    .Include(x => x.Variants)
    .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
    {
        TempData["Error"] = "Producto no encontrado.";
        return RedirectToAction(nameof(Index));
    }

    return View(product);
}

[HttpGet]
public async Task<IActionResult> PublishToMercadoLibre(int id, int? marketplaceAccountId = null)
{
   var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

var product = await _context.Products
    .Include(x => x.MarketplacePublications)
    .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
        return NotFound();

    var isUsdSource = string.Equals(product.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(product.SourceMarketplace, "eBay", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(product.SourceMarketplace, "Amazon", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(product.SourceMarketplace, "AliExpress", StringComparison.OrdinalIgnoreCase);

    decimal initialPrice = product.SourcePrice ?? 0m;
    decimal? initialUnitCost = string.Equals(product.SourceMarketplace, "MercadoLibreInsights", StringComparison.OrdinalIgnoreCase)
        ? null
        : product.SourcePrice;

    if (product.SourcePrice.HasValue && product.SourcePrice.Value > 0 && isUsdSource)
    {
        var crossBorderSettings = await _crossBorderPricingService.GetOrCreateSettingsAsync(organizationId);
        var pricing = _crossBorderPricingService.CalculatePricing(product.SourcePrice.Value, crossBorderSettings);
        initialPrice = pricing.SuggestedListingPriceMxn;
        initialUnitCost = pricing.LandedCostJuarezMxn;
    }

    var model = new PublishToMercadoLibreRequest
    {
        ProductId = product.Id,
        Title = !string.IsNullOrWhiteSpace(product.Title)
            ? product.Title.Length > 60 ? product.Title.Substring(0, 60) : product.Title
            : "Producto sin título",

        Description = product.Description,
        Price = initialPrice,
        Stock = product.SourceStock ?? 1,
        CurrencyId = "MXN",
        CategoryId = "",
        Condition = "new",
        ListingTypeId = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DefaultListingTypeId,
        ImageUrl = product.ImageUrl,
        Brand = product.Brand,
        Model = product.Model,
        MarketplaceAccountId = marketplaceAccountId,
        UnitCost = initialUnitCost
    };

    // La calculadora arranca con lo que cobra Mercado Libre según Costos de cruce (retención y envío).
    var sellingSettings = await _crossBorderPricingService.GetOrCreateSettingsAsync(organizationId);
    model.TaxRate = sellingSettings.PlatformTaxWithholdingPercent;
    if (model.Price >= 299m)
        model.ShippingCost = sellingSettings.EstimatedShippingMxn;

    await PopulateMarketplaceAccountsAsync(model, organizationId);

    var publication = product.MarketplacePublications.FirstOrDefault(x =>
        x.Marketplace == "MercadoLibre" &&
        x.MarketplaceAccountId == model.MarketplaceAccountId)
        ?? product.MarketplacePublications.FirstOrDefault(x =>
            x.Marketplace == "MercadoLibre" && x.MarketplaceAccountId == null);

    if (publication != null)
    {
        model.Price = publication.Price ?? model.Price;
        model.Stock = publication.Stock ?? model.Stock;
        model.CurrencyId = publication.CurrencyId ?? model.CurrencyId;
        model.CategoryId = publication.CategoryId ?? model.CategoryId;
        model.Condition = publication.Condition ?? model.Condition;
        model.ListingTypeId = publication.ListingTypeId ?? model.ListingTypeId;
    }

    if (!string.IsNullOrWhiteSpace(model.CategoryId))
    {
        try
        {
            model.Attributes = await GetRequiredAttributesAsync(
                model.CategoryId,
                model.MarketplaceAccountId);
            model.AttributesLoaded = true;
            PrefillKnownAttributes(model);
            ApplySavedAttributes(model, publication?.AttributesJson);
            await AutoCompleteAsync(model, product, organizationId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not automatically load Mercado Libre attributes for category {CategoryId}.",
                model.CategoryId);
            model.AttributeLoadError = "No fue posible cargar automáticamente los atributos. Usa “Cargar atributos” para volver a intentar.";
        }
    }

    return View(model);
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> EstimateMercadoLibreFees(
    MercadoLibreFeeEstimateRequest request,
    CancellationToken cancellationToken)
{
    if (!ModelState.IsValid || !request.MarketplaceAccountId.HasValue)
        return BadRequest(new { message = "Completa cuenta, categoría, precio y tipo de publicación." });

    var organizationId = await _organizationContext
        .RequireCurrentOrganizationIdAsync(cancellationToken);
    var accountExists = await _context.MarketplaceAccounts
        .AsNoTracking()
        .AnyAsync(x => x.Id == request.MarketplaceAccountId.Value &&
                       x.OrganizationId == organizationId &&
                       x.Marketplace == "MercadoLibre" &&
                       x.Status == "Connected" &&
                       x.Tokens.Any(token => token.IsActive),
            cancellationToken);
    if (!accountExists)
        return BadRequest(new { message = "La cuenta de Mercado Libre no está conectada." });

    try
    {
        var accessToken = await _mercadoLibreAuthService.GetValidAccessTokenAsync(
            organizationId,
            request.MarketplaceAccountId.Value);
        var estimate = await _mercadoLibreFeeService.EstimateAsync(
            accessToken,
            request.CategoryId.Trim().ToUpperInvariant(),
            request.Price,
            request.CurrencyId.Trim().ToUpperInvariant(),
            request.ListingTypeId,
            cancellationToken);
        return Json(estimate);
    }
    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
    {
        _logger.LogWarning(
            exception,
            "Could not estimate Mercado Libre fees for category {CategoryId}.",
            request.CategoryId);
        return BadRequest(new
        {
            message = "Mercado Libre no pudo calcular la comisión. Verifica categoría, precio y tipo de publicación."
        });
    }
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> PublishToMercadoLibre(PublishToMercadoLibreRequest input)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == input.ProductId && x.OrganizationId == organizationId);

    if (product == null)
        return NotFound();

    ValidateRequiredMercadoLibreAttributes(input);

    // Palabras prohibidas, datos de contacto o más de 60 caracteres: Mercado Libre lo rechaza o lo castiga.
    foreach (var issue in MarketplaceSync.Services.Ai.TitleQuality.Review(input.Title).Where(i => i.Blocks))
        ModelState.AddModelError(nameof(input.Title), issue.Message + $" Sugerencia: «{MarketplaceSync.Services.Ai.TitleQuality.Clean(input.Title)}».");

    if (!ModelState.IsValid)
    {
        await PopulateMarketplaceAccountsAsync(input, organizationId);
        await RepopulateMercadoLibreAttributeOptionsAsync(input);
        return View(input);
    }

var appUserName = User.Identity?.Name;

if (string.IsNullOrWhiteSpace(appUserName))
{
    return RedirectToAction("Login", "Account");
}

var token = await _context.MercadoLibreTokens
    .Where(x => x.OrganizationId == organizationId &&
                x.MarketplaceAccountId == input.MarketplaceAccountId &&
                x.IsActive)
    .OrderByDescending(x => x.UpdatedAt)
    .FirstOrDefaultAsync();    

    if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
    {
        input.ErrorMessage = "No hay token activo de Mercado Libre. Conecta primero la cuenta.";
        await PopulateMarketplaceAccountsAsync(input, organizationId);
        return View(input);
    }

    var publication = await _context.MarketplacePublications
        .FirstOrDefaultAsync(x => x.ProductId == product.Id &&
                                  x.Marketplace == "MercadoLibre" &&
                                  x.MarketplaceAccountId == token.MarketplaceAccountId);
    var updatingExistingItem = !string.IsNullOrWhiteSpace(publication?.ExternalItemId);

    var variants = await _context.ProductVariants
        .Where(x => x.ProductId == product.Id && x.OrganizationId == organizationId && x.IsActive)
        .OrderBy(x => x.Sku)
        .ToListAsync();

    var publicationVariantLinks = publication == null
        ? new List<MarketplacePublicationVariant>()
        : await _context.MarketplacePublicationVariants
            .Where(x => x.MarketplacePublicationId == publication.Id)
            .ToListAsync();

    if (variants.Count == 0 && publicationVariantLinks.Count > 0)
    {
        input.ErrorMessage = "Esta publicación ya tiene variantes en Mercado Libre. Mantén al menos una variante activa para evitar desincronizarla.";
        await PopulateMarketplaceAccountsAsync(input, organizationId);
        return View(input);
    }

    if (publicationVariantLinks.Any(link => variants.All(x => x.Id != link.ProductVariantId)))
    {
        input.ErrorMessage = "Una variante ya asociada a la publicación está inactiva. Reactívala antes de sincronizar.";
        await PopulateMarketplaceAccountsAsync(input, organizationId);
        return View(input);
    }

    var generalReserve = Math.Max(0, await _context.AutomationSettings.AsNoTracking()
        .Where(a => a.OrganizationId == organizationId).Select(a => a.StockReserve).FirstOrDefaultAsync());

    Dictionary<string, object?> BuildPayload()
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = input.Title,
            ["category_id"] = input.CategoryId,
            ["currency_id"] = input.CurrencyId,
            ["buying_mode"] = "buy_it_now",
            ["condition"] = input.Condition,
            ["listing_type_id"] = input.ListingTypeId,
            ["pictures"] = string.IsNullOrWhiteSpace(input.ImageUrl)
                ? Array.Empty<object>()
                : new object[] { new { source = input.ImageUrl } },
            ["attributes"] = BuildMercadoLibreAttributes(input)
        };

        if (variants.Count > 0)
        {
            var variantLinksById = publicationVariantLinks.ToDictionary(x => x.ProductVariantId);
            payload["variations"] = variants.Select(variant =>
            {
                var variation = new Dictionary<string, object?>
                {
                    ["attribute_combinations"] = JsonSerializer
                        .Deserialize<Dictionary<string, string>>(variant.AttributesJson)!
                        .Select(attribute => new
                        {
                            id = attribute.Key,
                            name = attribute.Key,
                            value_name = attribute.Value
                        })
                        .ToArray(),
                    ["price"] = variant.Price ?? input.Price,
                    ["available_quantity"] = MarketplaceSync.Services.Inventory.StockReserve.Publishable(variant, generalReserve),
                    ["seller_custom_field"] = variant.Sku
                };

                if (variantLinksById.TryGetValue(variant.Id, out var link) &&
                    long.TryParse(link.ExternalVariationId, out var externalVariationId))
                    variation["id"] = externalVariationId;

                return variation;
            }).ToArray();
        }
        else
        {
            payload["price"] = input.Price;
            payload["available_quantity"] = input.Stock;
        }
        return payload;
    }

    var client = _httpClientFactory.CreateClient();
    var targetUrl = updatingExistingItem
        ? $"{_listingClient.ApiBase}/items/{Uri.EscapeDataString(publication!.ExternalItemId!)}"
        : $"{_listingClient.ApiBase}/items";
    var content = string.Empty;
    var autoFilledByRetry = new List<string>();

    // Si Mercado Libre rechaza por atributos que faltan, se llenan con IA y se reintenta una vez.
    for (var attempt = 1; ; attempt++)
    {
        using var request = new HttpRequestMessage(
            updatingExistingItem ? HttpMethod.Put : HttpMethod.Post,
            targetUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(BuildPayload()), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        content = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            break;

        _logger.LogWarning("Mercado Libre rejected product {ProductId} in {CategoryId} ({ListingType}), attempt {Attempt}: {Body}",
            product.Id, input.CategoryId, input.ListingTypeId, attempt, content.Length > 2000 ? content[..2000] : content);
        var missing = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.ParseMissingAttributes(content);

        if (attempt == 1 && missing.Any)
        {
            await RepopulateMercadoLibreAttributeOptionsAsync(input, missing.All);
            FlagMissingAttributes(input, missing);
            var askedIds = input.Attributes.Where(a => a.Flagged).Select(a => a.Id).ToList();
            await AutoCompleteAsync(input, product, organizationId, askedIds);
            var reasonChosen = input.Attributes.Any(a =>
                a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.EmptyGtinReasonId &&
                (!string.IsNullOrWhiteSpace(a.ValueId) || !string.IsNullOrWhiteSpace(a.ValueName)));
            var stillMissing = input.Attributes.Any(a => a.Flagged &&
                string.IsNullOrWhiteSpace(a.ValueId) && string.IsNullOrWhiteSpace(a.ValueName) &&
                !(a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId && reasonChosen));
            if (!stillMissing)
            {
                autoFilledByRetry.AddRange(input.Attributes
                    .Where(a => a.Flagged)
                    .Select(a => $"{(a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId ? "código de barras" : a.Name)} = {a.ValueName ?? a.ValueId}"));
                continue;
            }
        }

        input.MercadoLibreRawError = content.Length > 4000 ? content[..4000] : content;
        // Lo que Mercado Libre reclamó se agrega al formulario y se marca para que se vea qué llenar.
        await PopulateMarketplaceAccountsAsync(input, organizationId);
        await RepopulateMercadoLibreAttributeOptionsAsync(input, missing.All);
        var flagged = FlagMissingAttributes(input, missing);
        input.ErrorMessage = "Mercado Libre rechazó la publicación: " +
            MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DescribeError(
                content,
                input.Attributes.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase));
        ModelState.Clear();
        foreach (var index in flagged)
            ModelState.AddModelError($"Attributes[{index}].ValueName", "Mercado Libre pide este dato para publicar.");
        return View(input);
    }

    using var document = JsonDocument.Parse(content);
    var root = document.RootElement;

    publication ??= await GetOrCreateMarketplacePublicationAsync(product, token.MarketplaceAccountId);
    publication.ExternalItemId = root.TryGetProperty("id", out var idElement)
        ? idElement.GetString()
        : null;

    publication.Permalink = root.TryGetProperty("permalink", out var permalinkElement)
        ? permalinkElement.GetString()
        : null;

    publication.Status = root.TryGetProperty("status", out var statusElement)
        ? statusElement.GetString()
        : "published";

    publication.CategoryId = input.CategoryId;
    publication.Price = input.Price;
    publication.Stock = input.Stock;
    publication.CurrencyId = input.CurrencyId;
    publication.ListingTypeId = input.ListingTypeId;
    publication.Condition = input.Condition;
    publication.PublishedAt = DateTime.UtcNow;
    publication.IsPublished = true;
    publication.UpdatedAt = DateTime.UtcNow;
    publication.AttributesJson = JsonSerializer.Serialize(input.Attributes
        .Where(x => !string.IsNullOrWhiteSpace(x.ValueId) || !string.IsNullOrWhiteSpace(x.ValueName))
        .Select(x => new MarketplaceSync.Services.MercadoLibre.SavedAttributeValue(
            x.Id,
            x.ValueId?.StartsWith("name:", StringComparison.Ordinal) == true ? null : x.ValueId,
            x.ValueId?.StartsWith("name:", StringComparison.Ordinal) == true ? x.ValueId[5..] : x.ValueName)));
    product.Status = "ReadyToPublish";

    // El código de barras capturado aquí se guarda en la única variante si aún no tenía.
    var publishedGtin = input.Attributes.FirstOrDefault(a => a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId)?.ValueName;
    if (!string.IsNullOrWhiteSpace(publishedGtin) && variants.Count == 1 && string.IsNullOrWhiteSpace(variants[0].Barcode))
        variants[0].Barcode = publishedGtin;

    await _context.SaveChangesAsync();

    // La descripción se guarda en un recurso aparte de Mercado Libre.
    if (!string.IsNullOrWhiteSpace(input.Description) && !string.IsNullOrWhiteSpace(publication.ExternalItemId))
    {
        await _listingClient.UpsertDescriptionAsync(
            token.AccessToken, publication.ExternalItemId, input.Description, updatingExistingItem);
    }
    if (!string.IsNullOrWhiteSpace(input.Description))
        product.Description = input.Description;

    if (variants.Count > 0 && root.TryGetProperty("variations", out var returnedVariations) &&
        returnedVariations.ValueKind == JsonValueKind.Array)
    {
        var returnedBySku = returnedVariations.EnumerateArray()
            .Where(x => x.TryGetProperty("seller_custom_field", out var skuElement) && skuElement.ValueKind == JsonValueKind.String)
            .ToDictionary(x => x.GetProperty("seller_custom_field").GetString()!, StringComparer.OrdinalIgnoreCase);

        foreach (var variant in variants)
        {
            JsonElement externalVariation;
            if (!returnedBySku.TryGetValue(variant.Sku, out externalVariation))
            {
                externalVariation = returnedVariations.EnumerateArray()
                    .FirstOrDefault(item => ReturnedVariationMatches(item, variant.AttributesJson));
                if (externalVariation.ValueKind == JsonValueKind.Undefined)
                    continue;
            }

            if (!externalVariation.TryGetProperty("id", out var externalId))
                continue;

            var link = publicationVariantLinks.FirstOrDefault(x => x.ProductVariantId == variant.Id);
            if (link == null)
            {
                link = new MarketplacePublicationVariant
                {
                    MarketplacePublicationId = publication.Id,
                    ProductVariantId = variant.Id
                };
                _context.MarketplacePublicationVariants.Add(link);
                publicationVariantLinks.Add(link);
            }

            link.ExternalVariationId = externalId.ValueKind == JsonValueKind.String
                ? externalId.GetString()
                : externalId.GetRawText();
        }

        await _context.SaveChangesAsync();
    }

    await _context.SaveChangesAsync();
    TempData["Success"] = (updatingExistingItem
        ? "Publicación actualizada en Mercado Libre."
        : "Publicado en Mercado Libre.") +
        (autoFilledByRetry.Count > 0 ? " Mercado Libre pidió más datos y los completé sola: " + string.Join(", ", autoFilledByRetry) + "." : "");
    return RedirectToAction(nameof(Details), new { id = product.Id });
}
private static object[] BuildMercadoLibreAttributes(PublishToMercadoLibreRequest model)
{
    var attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    var hasGtin = model.Attributes.Any(a =>
        a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId &&
        !string.IsNullOrWhiteSpace(a.ValueName));

    foreach (var attribute in model.Attributes)
    {
        if (string.IsNullOrWhiteSpace(attribute.Id))
            continue;
        // Con código de barras no se manda el motivo de "no tiene".
        if (hasGtin && attribute.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.EmptyGtinReasonId)
            continue;

        var selectedOption = attribute.Values.FirstOrDefault(option =>
            !string.IsNullOrWhiteSpace(attribute.ValueName) &&
            string.Equals(option.Name, attribute.ValueName, StringComparison.OrdinalIgnoreCase));
        var valueId = !string.IsNullOrWhiteSpace(attribute.ValueId)
            ? attribute.ValueId
            : selectedOption?.Id;
        var valueName = attribute.ValueName;

        if (valueId?.StartsWith("name:", StringComparison.Ordinal) == true)
        {
            valueName = valueId[5..];
            valueId = null;
        }

        if (!string.IsNullOrWhiteSpace(valueId))
        {
            attributes[attribute.Id] = new
            {
                id = attribute.Id,
                value_id = valueId
            };
        }
        else if (!string.IsNullOrWhiteSpace(valueName))
        {
            attributes[attribute.Id] = new
            {
                id = attribute.Id,
                value_name = valueName.Trim()
            };
        }
    }

    if (!string.IsNullOrWhiteSpace(model.Brand) && !attributes.ContainsKey("BRAND"))
    {
        attributes["BRAND"] = new
        {
            id = "BRAND",
            value_name = model.Brand
        };
    }

    if (!string.IsNullOrWhiteSpace(model.Model) && !attributes.ContainsKey("MODEL"))
    {
        attributes["MODEL"] = new
        {
            id = "MODEL",
            value_name = model.Model
        };
    }

    return attributes.Values.ToArray();
}

private async Task RepopulateMercadoLibreAttributeOptionsAsync(
    PublishToMercadoLibreRequest model,
    IEnumerable<string>? alsoInclude = null)
{
    if (string.IsNullOrWhiteSpace(model.CategoryId))
        return;

    try
    {
        var submittedAttributes = model.Attributes
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Se piden también los que ya estaban en el formulario para no perder lo que Mercado Libre reclamó antes.
        var refreshedAttributes = await GetRequiredAttributesAsync(
            model.CategoryId,
            model.MarketplaceAccountId,
            submittedAttributes.Keys.Concat(alsoInclude ?? Array.Empty<string>()));

        foreach (var refreshedAttribute in refreshedAttributes)
        {
            if (!submittedAttributes.TryGetValue(refreshedAttribute.Id, out var submittedAttribute))
                continue;

            refreshedAttribute.ValueId = submittedAttribute.ValueId;
            refreshedAttribute.ValueName = submittedAttribute.ValueName;
            if (submittedAttribute.Flagged)
            {
                refreshedAttribute.Flagged = true;
                refreshedAttribute.Required = true;
                refreshedAttribute.Conditional = false;
            }
        }

        // Un GTIN que Mercado Libre pidió pero que la categoría no trae en su esquema se conserva.
        var gtinId = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId;
        if (refreshedAttributes.All(a => a.Id != gtinId) &&
            submittedAttributes.TryGetValue(gtinId, out var submittedGtin) && submittedGtin.Flagged)
            refreshedAttributes.Insert(0, submittedGtin);

        model.Attributes = refreshedAttributes;
        model.AttributesLoaded = true;
    }
    catch (Exception exception)
    {
        _logger.LogWarning(exception, "Could not repopulate Mercado Libre attribute options for {CategoryId}.", model.CategoryId);
    }
}

/// <summary>Marca como obligatorios los atributos que Mercado Libre reclamó. Regresa sus posiciones.</summary>
private static List<int> FlagMissingAttributes(
    PublishToMercadoLibreRequest model,
    MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.MissingAttributes missing)
{
    var flagged = new List<int>();
    if (!missing.Any)
        return flagged;

    var ids = missing.Ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var names = missing.Names
        .Select(MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.NormalizeName)
        .ToHashSet();

    // Si pidió el GTIN y la categoría no lo trae en el esquema, se agrega a mano.
    if (ids.Contains(MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId) &&
        model.Attributes.All(a => a.Id != MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId))
    {
        model.Attributes.Insert(0, new MercadoLibreAttributeInput
        {
            Id = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId,
            Name = "Código universal de producto",
            ValueType = "string"
        });
    }

    for (var index = 0; index < model.Attributes.Count; index++)
    {
        var attribute = model.Attributes[index];
        var asked = ids.Contains(attribute.Id) ||
                    names.Contains(MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.NormalizeName(attribute.Name));
        if (!asked)
            continue;

        attribute.Flagged = true;
        attribute.Required = true;
        attribute.Conditional = false;
        if (string.IsNullOrWhiteSpace(attribute.ValueId) && string.IsNullOrWhiteSpace(attribute.ValueName))
            flagged.Add(index);
    }

    // Con el GTIN reclamado, la razón de "no tiene" cuenta como respuesta.
    var gtinIndex = model.Attributes.FindIndex(a => a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId);
    var hasReason = model.Attributes.Any(a =>
        a.Id == MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.EmptyGtinReasonId &&
        (!string.IsNullOrWhiteSpace(a.ValueId) || !string.IsNullOrWhiteSpace(a.ValueName)));
    if (gtinIndex >= 0 && hasReason)
        flagged.Remove(gtinIndex);

    return flagged;
}

private void ValidateRequiredMercadoLibreAttributes(PublishToMercadoLibreRequest model)
{
    const string gtinId = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.GtinId;
    const string reasonId = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.EmptyGtinReasonId;
    static bool Filled(MercadoLibreAttributeInput a) =>
        !string.IsNullOrWhiteSpace(a.ValueId) || !string.IsNullOrWhiteSpace(a.ValueName);

    var reason = model.Attributes.FirstOrDefault(a => a.Id == reasonId);
    for (var index = 0; index < model.Attributes.Count; index++)
    {
        var attribute = model.Attributes[index];

        if (attribute.Id == gtinId)
        {
            if (Filled(attribute))
            {
                // Nunca se publica un código inventado o mal copiado: se revisa el dígito verificador.
                var problem = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.ValidateGtin(attribute.ValueName, out var digits);
                if (problem is not null)
                    ModelState.AddModelError($"Attributes[{index}].ValueName", problem);
                else
                    attribute.ValueName = digits;
            }
            else if (attribute.Required && (reason is null || !Filled(reason)))
            {
                ModelState.AddModelError(
                    $"Attributes[{index}].ValueName",
                    reason is null
                        ? "Escanea o escribe el código de barras del producto."
                        : "Escanea o escribe el código de barras, o abajo elige por qué no tiene.");
            }
            continue;
        }

        if (attribute.Id == reasonId)
            continue;

        if (!attribute.Required)
            continue;

        if (string.IsNullOrWhiteSpace(attribute.ValueId) &&
            string.IsNullOrWhiteSpace(attribute.ValueName))
        {
            ModelState.AddModelError(
                $"Attributes[{index}].ValueName",
                $"Completa el atributo {attribute.Name}.");
        }
    }
}
public async Task<IActionResult> CreateFromUrl()
{
    // El interruptor "publicar en automático" arranca como lo configuraste en Costos de cruce.
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var settings = await _crossBorderPricingService.GetOrCreateSettingsAsync(organizationId);
    ViewBag.AutoPublishDefault = settings.AutoPublishEnabled;
    return View(new CreateProductFromUrlViewModel());
}
// GET: /Products
public async Task<IActionResult> Index(string? q, ProductStage? stage)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

    var products = await _context.Products
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId)
        .Include(x => x.MarketplacePublications)
        .Include(x => x.Variants)
        .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
        .ToListAsync();

    var rows = products
        .Select(product =>
        {
            var publication = product.MarketplacePublications
                .Where(p => p.Marketplace == "MercadoLibre")
                .OrderByDescending(p => p.IsPublished)
                .ThenByDescending(p => p.UpdatedAt)
                .FirstOrDefault();
            var activeVariants = product.Variants.Where(v => v.IsActive).ToList();

            return new ProductListRow
            {
                Product = product,
                Publication = publication,
                Stage = ProductListRow.ResolveStage(product, publication),
                OwnStock = activeVariants.Sum(v => Math.Max(0, v.Stock)),
                SkuCount = activeVariants.Count
            };
        })
        .ToList();

    var counts = rows
        .GroupBy(x => x.Stage)
        .ToDictionary(g => g.Key, g => g.Count());

    var search = q?.Trim();
    IEnumerable<ProductListRow> filtered = rows;
    if (!string.IsNullOrWhiteSpace(search))
    {
        filtered = filtered.Where(x =>
            Contains(x.Product.Title, search) ||
            Contains(x.Product.Brand, search) ||
            Contains(x.Product.Model, search) ||
            Contains(x.Product.SourceProductId, search) ||
            Contains(x.Publication?.ExternalItemId, search) ||
            x.Product.Variants.Any(v => Contains(v.Sku, search)));
    }

    if (stage.HasValue)
        filtered = filtered.Where(x => x.Stage == stage.Value);

    return View(new ProductListViewModel
    {
        Rows = filtered.ToList(),
        EnglishTitleCount = rows.Count(x => x.Publication?.IsPublished != true &&
                                            MarketplaceSync.Services.Ai.ProductTitleTranslator.LooksEnglish(x.Product.Title)),
        Counts = counts,
        TotalCount = rows.Count,
        Search = search,
        Stage = stage
    });

    static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}

[HttpGet]
public async Task<IActionResult> Variants(int id)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .AsNoTracking()
        .Where(x => x.Id == id && x.OrganizationId == organizationId)
        .Select(x => new { x.Id, x.Title })
        .FirstOrDefaultAsync();

    if (product == null)
        return NotFound();

    var variants = await _context.ProductVariants
        .AsNoTracking()
        .Where(x => x.ProductId == id && x.OrganizationId == organizationId)
        .OrderBy(x => x.Sku)
        .Select(x => new ProductVariantRowViewModel
        {
            Id = x.Id,
            Sku = x.Sku,
            Attributes = FormatVariantAttributes(x.AttributesJson),
            Barcode = x.Barcode,
            Price = x.Price,
            Stock = x.Stock,
            IsActive = x.IsActive
        })
        .ToListAsync();

    return View(new ProductVariantsViewModel
    {
        ProductId = product.Id,
        ProductTitle = product.Title ?? "Producto sin título",
        NewVariant = new ProductVariantInput { ProductId = product.Id },
        Variants = variants
    });
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AddVariant([Bind(Prefix = "NewVariant")] ProductVariantInput input)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products.FirstOrDefaultAsync(x =>
        x.Id == input.ProductId && x.OrganizationId == organizationId);
    if (product == null)
        return NotFound();

    Dictionary<string, string>? attributes = null;
    try
    {
        attributes = ParseVariantAttributes(input.Attributes ?? string.Empty);
    }
    catch (FormatException exception)
    {
        ModelState.AddModelError("NewVariant.Attributes", exception.Message);
    }

    var sku = input.Sku?.Trim().ToUpperInvariant() ?? string.Empty;
    if (await _context.ProductVariants.AnyAsync(x =>
            x.OrganizationId == organizationId && x.Sku == sku))
        ModelState.AddModelError("NewVariant.Sku", "Ese SKU ya está asignado en esta organización.");

    if (!ModelState.IsValid || attributes == null)
    {
        TempData["VariantError"] = "Revisa los datos de la variante e intenta de nuevo.";
        return RedirectToAction(nameof(Variants), new { id = input.ProductId });
    }

    _context.ProductVariants.Add(new ProductVariant
    {
        ProductId = product.Id,
        OrganizationId = organizationId,
        Sku = sku,
        AttributesJson = JsonSerializer.Serialize(attributes),
        Barcode = string.IsNullOrWhiteSpace(input.Barcode) ? null : input.Barcode.Trim(),
        Price = input.Price,
        Stock = input.Stock,
        IsActive = input.IsActive,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    });

    await _context.SaveChangesAsync();
    TempData["Success"] = $"Variante {sku} agregada.";
    return RedirectToAction(nameof(Variants), new { id = input.ProductId });
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> UpdateVariant(ProductVariantInput input)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var variant = await _context.ProductVariants.FirstOrDefaultAsync(x =>
        x.Id == input.Id && x.ProductId == input.ProductId && x.OrganizationId == organizationId);
    if (variant == null)
        return NotFound();

    Dictionary<string, string>? attributes = null;
    try
    {
        attributes = ParseVariantAttributes(input.Attributes ?? string.Empty);
    }
    catch (FormatException exception)
    {
        ModelState.AddModelError(nameof(input.Attributes), exception.Message);
    }

    var sku = input.Sku?.Trim().ToUpperInvariant() ?? string.Empty;
    if (await _context.ProductVariants.AnyAsync(x =>
            x.OrganizationId == organizationId && x.Sku == sku && x.Id != variant.Id))
        ModelState.AddModelError(nameof(input.Sku), "Ese SKU ya está asignado en esta organización.");

    var isPublishedVariant = await _context.MarketplacePublicationVariants
        .AnyAsync(x => x.ProductVariantId == variant.Id);
    if (isPublishedVariant && attributes != null &&
        !VariantAttributesMatch(variant.AttributesJson, attributes))
        ModelState.AddModelError(nameof(input.Attributes), "Los atributos de una variante publicada no se pueden cambiar; agrega una nueva variante en su lugar.");
    if (isPublishedVariant && !input.IsActive)
        ModelState.AddModelError(nameof(input.IsActive), "Esta variante está publicada. Retírala primero de Mercado Libre antes de desactivarla.");

    if (!ModelState.IsValid || attributes == null)
    {
        TempData["VariantError"] = "No se guardaron los cambios. Revisa SKU, atributos, precio y stock.";
        return RedirectToAction(nameof(Variants), new { id = input.ProductId });
    }

    variant.Sku = sku;
    variant.AttributesJson = JsonSerializer.Serialize(attributes);
    variant.Barcode = string.IsNullOrWhiteSpace(input.Barcode) ? null : input.Barcode.Trim();
    variant.Price = input.Price;
    variant.Stock = input.Stock;
    variant.IsActive = input.IsActive;
    variant.UpdatedAt = DateTime.UtcNow;
    await _context.SaveChangesAsync();

    TempData["Success"] = $"Variante {sku} actualizada.";
    return RedirectToAction(nameof(Variants), new { id = input.ProductId });
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteVariant(int id, int productId)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var variant = await _context.ProductVariants.FirstOrDefaultAsync(x =>
        x.Id == id && x.ProductId == productId && x.OrganizationId == organizationId);
    if (variant == null)
        return NotFound();

    if (await _context.MarketplacePublicationVariants.AnyAsync(x => x.ProductVariantId == variant.Id))
    {
        TempData["VariantError"] = "Esta variante está publicada. Retírala de Mercado Libre antes de eliminarla.";
        return RedirectToAction(nameof(Variants), new { id = productId });
    }

    _context.ProductVariants.Remove(variant);
    await _context.SaveChangesAsync();
    TempData["Success"] = $"Variante {variant.Sku} eliminada.";
    return RedirectToAction(nameof(Variants), new { id = productId });
}

private static Dictionary<string, string> ParseVariantAttributes(string input)
{
    var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in input.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
    {
        var separator = line.IndexOf('=');
        if (separator <= 0 || separator == line.Length - 1)
            throw new FormatException("Usa pares ID=valor separados por punto y coma, por ejemplo COLOR=Rojo; SIZE=M.");

        var id = line[..separator].Trim().ToUpperInvariant();
        var value = line[(separator + 1)..].Trim();
        id = Regex.Replace(id, @"\s+", "_");
        if (!Regex.IsMatch(id, @"^[A-Z0-9_]+$") || string.IsNullOrWhiteSpace(value))
            throw new FormatException("Cada atributo debe tener un ID válido y un valor.");
        if (!attributes.TryAdd(id, value))
            throw new FormatException($"El atributo {id} está repetido.");
    }

    if (attributes.Count == 0)
        throw new FormatException("Agrega al menos un atributo, por ejemplo COLOR=Rojo.");

    return attributes;
}

private static string FormatVariantAttributes(string attributesJson)
{
    try
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(attributesJson);
        return values == null ? string.Empty : string.Join("; ", values.Select(x => $"{x.Key}={x.Value}"));
    }
    catch (JsonException)
    {
        return string.Empty;
    }
}

private static bool VariantAttributesMatch(string attributesJson, Dictionary<string, string> attributes)
{
    try
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(attributesJson);
        return existing != null && existing.Count == attributes.Count &&
               existing.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                   .SequenceEqual(attributes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase));
    }
    catch (JsonException)
    {
        return false;
    }
}

private static bool ReturnedVariationMatches(JsonElement variation, string attributesJson)
{
    if (!variation.TryGetProperty("attribute_combinations", out var combinations) ||
        combinations.ValueKind != JsonValueKind.Array)
        return false;

    var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(attributesJson);
    if (expected == null)
        return false;

    var actual = combinations.EnumerateArray()
        .Where(x => x.TryGetProperty("id", out _) && x.TryGetProperty("value_name", out _))
        .ToDictionary(
            x => x.GetProperty("id").GetString() ?? string.Empty,
            x => x.GetProperty("value_name").GetString() ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

    return expected.Count == actual.Count && expected.All(x =>
        actual.TryGetValue(x.Key, out var value) &&
        string.Equals(value, x.Value, StringComparison.OrdinalIgnoreCase));
}

// GET: /Products/Edit/5
[HttpGet]
public async Task<IActionResult> Edit(int id)
{
   var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

var product = await _context.Products
    .Include(x => x.MarketplacePublications)
    .ThenInclude(x => x.MarketplaceAccount)
    .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
    {
        return NotFound();
    }

    return View(product);
}

// POST: /Products/Edit/5
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Edit(int id, Product input)
{
    if (id != input.Id)
    {
        return BadRequest();
    }

    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .Include(x => x.MarketplacePublications)
        .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
    {
        return NotFound();
    }

    ModelState.Remove(nameof(Product.CreatedByUserId));
    ModelState.Remove(nameof(Product.CreatedByUser));
    ModelState.Remove(nameof(Product.Organization));

    if (!ModelState.IsValid)
    {
        return View(input);
    }

    product.Title = input.Title;
    product.Description = input.Description;
    product.Brand = input.Brand;
    product.Model = input.Model;
    product.ImageUrl = input.ImageUrl;

    product.SourcePrice = input.SourcePrice;
    product.SourceCurrency = input.SourceCurrency;
    product.SourceStock = input.SourceStock;
    product.SourceStatus = input.SourceStatus;
    product.SourceAvailabilityText = input.SourceAvailabilityText;

    product.Status = input.Status;
    product.UpdatedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync();

    TempData["Success"] = "Producto actualizado correctamente.";

    return RedirectToAction(nameof(Edit), new { id = product.Id });
}

// POST: /Products/PrepareForMercadoLibre/5
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> PrepareForMercadoLibre(int id)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
    {
        return NotFound();
    }

    var publication = await GetOrCreateMarketplacePublicationAsync(product);
    publication.CurrencyId = "MXN";
    publication.Condition = "new";
    publication.ListingTypeId = MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DefaultListingTypeId;

    if (product.SourceStock.HasValue)
    {
        publication.Stock = product.SourceStock.Value;
    }

    if (product.SourcePrice.HasValue)
    {
        if (string.Equals(product.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            // Temporal: tipo de cambio fijo + margen
            publication.Price = Math.Round(product.SourcePrice.Value * 18.50m * 1.25m, 2);
        }
        else
        {
            // Si ya viene en MXN, solo agrega margen
            publication.Price = Math.Round(product.SourcePrice.Value * 1.25m, 2);
        }
    }

    publication.UpdatedAt = DateTime.UtcNow;

    product.Status = "NeedsReview";
    product.UpdatedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync();

    TempData["Success"] = "Producto preparado para Mercado Libre. Revisa categoría, precio, stock y atributos.";

    return RedirectToAction(nameof(Edit), new { id = product.Id });
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> CreateFromUrl(CreateProductFromUrlViewModel model)
{
    if (!ModelState.IsValid)
    {
        return View(model);
    }

    if (string.IsNullOrWhiteSpace(model.SourceUrl))
    {
        ModelState.AddModelError(nameof(model.SourceUrl), "Ingresa un link válido de Amazon, eBay o Mercado Libre.");
        return View(model);
    }

    var extracted = await _extractor.ExtractAsync(model.SourceUrl.Trim());
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

    if (string.IsNullOrWhiteSpace(userId))
        return Challenge();

    var product = new Product
    {
        OrganizationId = organizationId,
        CreatedByUserId = userId,
        SourceUrl = extracted.SourceUrl ?? model.SourceUrl.Trim(),
        SourceMarketplace = extracted.SourceMarketplace ?? "UNKNOWN",
        SourceProductId = extracted.SourceProductId,

        Title = await _titleTranslator.ToSpanishAsync(extracted.Title),
        Description = extracted.Description,
        SourcePrice = extracted.SourcePrice,
        SourceCurrency = extracted.SourceCurrency,
        SourceStock = extracted.SourceStock,
        ImageUrl = extracted.ImageUrl,
        Brand = extracted.Brand,
        Model = extracted.Model,
        SourceGtin = extracted.Gtin,
        SourceGtinOrigin = extracted.Gtin is null ? null : extracted.SourceMarketplace,
        SourceGtinCheckedAt = MarketplaceSync.Services.Products.SourceBarcodeService.WasRead(extracted) ? DateTime.UtcNow : null,

        SourceStatus = extracted.SourceStatus,
        LastSourceCheckAt = DateTime.UtcNow,

        Status = "Draft",
        CreatedAt = DateTime.UtcNow,
    };

    if (model.ManualPriceUsd is > 0)
    {
        product.SourcePrice = Math.Round(model.ManualPriceUsd.Value, 2);
        product.SourceCurrency = "USD";
    }

    _context.Products.Add(product);
    await _context.SaveChangesAsync();

    if (extracted.SourceStatus is "Blocked" or "ScrapeFailed")
    {
        var missingParts = new List<string>();
        if (string.IsNullOrWhiteSpace(product.ImageUrl)) missingParts.Add("la foto");
        if (product.SourcePrice is not > 0) missingParts.Add("el precio");
        TempData["Warning"] = missingParts.Count == 0
            ? $"{product.SourceMarketplace} no dejó leer la página; el título se tomó del link. Revísalo antes de publicar."
            : $"{product.SourceMarketplace} no dejó leer la página desde el servidor. Falta {string.Join(" y ", missingParts)}: " +
              "complétalo en Editar (puedes copiar la dirección de la foto desde la tienda).";
    }
    else if (string.IsNullOrWhiteSpace(product.ImageUrl))
    {
        TempData["Warning"] = "No encontramos la foto del producto. Agrégala en Editar → Imagen URL antes de publicar.";
    }

    var actionValue = Request.Form["action"].ToString();
    if (actionValue is "enrich" or "ai")
    {
        var autoPublish = string.Equals(Request.Form["autoPublish"], "true", StringComparison.OrdinalIgnoreCase);
        return await RunAutopilotAndRedirectAsync(organizationId, product.Id, autoPublish);
    }

    TempData["Success"] = "Producto importado como borrador.";
    return RedirectToAction(nameof(Index));
}

/// <summary>Completa el producto con IA y, si se pide y todo está listo, lo publica.</summary>
/// <summary>Pasa al español los títulos en inglés de los productos que aún no se publican.</summary>
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> TranslateTitles(CancellationToken cancellationToken)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
    var products = await _context.Products
        .Where(x => x.OrganizationId == organizationId &&
                    !x.MarketplacePublications.Any(p => p.Marketplace == "MercadoLibre" && p.IsPublished))
        .OrderByDescending(x => x.CreatedAt)
        .Take(300)
        .ToListAsync(cancellationToken);

    var changed = 0;
    foreach (var product in products.Where(p => MarketplaceSync.Services.Ai.ProductTitleTranslator.LooksEnglish(p.Title)).Take(50))
    {
        var translated = await _titleTranslator.ToSpanishAsync(product.Title, cancellationToken);
        if (string.IsNullOrWhiteSpace(translated) || translated == product.Title)
            continue;
        product.Title = translated;
        product.UpdatedAt = DateTime.UtcNow;
        changed++;
    }
    await _context.SaveChangesAsync(cancellationToken);

    TempData["Success"] = changed == 0
        ? "No había títulos en inglés por traducir."
        : $"{changed} {(changed == 1 ? "título pasado" : "títulos pasados")} al español. Revísalos antes de publicar.";
    return RedirectToAction(nameof(Index));
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> CompleteWithAi(int id, bool publish = false)
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    return await RunAutopilotAndRedirectAsync(organizationId, id, publish);
}

private async Task<IActionResult> RunAutopilotAndRedirectAsync(Guid organizationId, int productId, bool publish)
{
    MarketplaceSync.Services.Ai.AutopilotResult result;
    try
    {
        result = await _autopilot.RunAsync(organizationId, productId, publish, HttpContext.RequestAborted);
    }
    catch (KeyNotFoundException)
    {
        return NotFound();
    }
    catch (Exception exception) when (!HttpContext.RequestAborted.IsCancellationRequested)
    {
        _logger.LogWarning(exception, "Autopilot failed for product {Id}.", productId);
        TempData["Error"] = "No fue posible completar el producto con IA. Intenta de nuevo en un momento.";
        return RedirectToAction(nameof(PublishToMercadoLibre), new { id = productId });
    }

    if (result.Published)
    {
        TempData["Success"] = "Publicado en Mercado Libre con IA. " + string.Join(" · ", result.Done);
        return RedirectToAction(nameof(Details), new { id = productId });
    }

    TempData["Success"] = (result.Ready
        ? "Listo para publicar. Revisa y presiona Publicar. "
        : "La IA preparó tu producto. ") + string.Join(" · ", result.Done);
    if (result.Missing.Count > 0)
        TempData["Warning"] = "Falta: " + string.Join(" · ", result.Missing);
    if (!result.AiConfigured)
        TempData["Error"] = "La IA no está configurada: agrega la variable Anthropic__ApiKey en Render para que escriba títulos, descripciones y atributos.";

    return RedirectToAction(nameof(PublishToMercadoLibre), new { id = productId });
}


    }
}
