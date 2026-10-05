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

public ProductsController(
    AppDbContext context,
    MarketplaceDetectorService detector,
    ProductExtractorService extractor,
    IHttpClientFactory httpClientFactory,
    MercadoLibreCategoryService mercadoLibreCategoryService,
    IOrganizationContext organizationContext
)
{
    _context = context;
    _detector = detector;
    _extractor = extractor;
    _httpClientFactory = httpClientFactory;
    _mercadoLibreCategoryService = mercadoLibreCategoryService;
    _organizationContext = organizationContext;
}
        
        private async Task<List<MercadoLibreAttributeInput>> GetRequiredAttributesAsync(
            string categoryId,
            int? marketplaceAccountId)
{
    var result = new List<MercadoLibreAttributeInput>();

    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
    var token = await _context.MercadoLibreTokens
        .Where(x => x.OrganizationId == organizationId &&
                    x.MarketplaceAccountId == marketplaceAccountId &&
                    x.IsActive)
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefaultAsync();

    if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        return result;

    var client = _httpClientFactory.CreateClient();

    var url = $"https://api.mercadolibre.com/categories/{Uri.EscapeDataString(categoryId)}/attributes";

    var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
    httpRequest.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

    var response = await client.SendAsync(httpRequest);
    var content = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        return result;

    var json = JsonNode.Parse(content)?.AsArray();

    if (json == null)
        return result;

    foreach (var node in json)
    {
        if (node == null)
            continue;

        var id = node["id"]?.GetValue<string>() ?? string.Empty;
        var name = node["name"]?.GetValue<string>() ?? id;
        var valueType = node["value_type"]?.GetValue<string>();

        var required = node["tags"]?["required"]?.GetValue<bool>() == true;

        if (!required)
            continue;

        var input = new MercadoLibreAttributeInput
        {
            Id = id,
            Name = name,
            Required = true,
            ValueType = valueType
        };

        var values = node["values"]?.AsArray();

        if (values != null)
        {
            foreach (var valueNode in values)
            {
                if (valueNode == null)
                    continue;

                var valueId = valueNode["id"]?.GetValue<string>();
                var valueName = valueNode["name"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(valueName))
                {
                    input.Values.Add(new MercadoLibreAttributeValueOption
                    {
                        Id = valueId,
                        Name = valueName
                    });
                }
            }
        }

        result.Add(input);
    }

    return result;
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
    request.Attributes = await GetRequiredAttributesAsync(request.CategoryId, request.MarketplaceAccountId);

    if (!request.Attributes.Any())
    {
        ModelState.AddModelError("", "No se encontraron atributos requeridos para esta categoría o no se pudo consultar Mercado Libre.");
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

        product.Title = extracted.Title ?? product.Title;
        product.Description = extracted.Description ?? product.Description;

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

        product.Title = extracted.Title ?? product.Title;
        product.Description = extracted.Description ?? product.Description;
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

            product.Title = extracted.Title ?? product.Title;
            product.Description = extracted.Description ?? product.Description;
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

    TempData["SuccessMessage"] = "Producto eliminado correctamente.";

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
public async Task<IActionResult> PublishToMercadoLibre(int id)
{
   var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

var product = await _context.Products
    .Include(x => x.MarketplacePublications)
    .FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId);

    if (product == null)
        return NotFound();

    var model = new PublishToMercadoLibreRequest
    {
        ProductId = product.Id,
        Title = !string.IsNullOrWhiteSpace(product.Title)
            ? product.Title.Length > 60 ? product.Title.Substring(0, 60) : product.Title
            : "Producto sin título",

        Description = product.Description,
        Price = product.SourcePrice ?? 0,
        Stock = product.SourceStock ?? 1,
        CurrencyId = "MXN",
        CategoryId = "",
        Condition = "new",
        ListingTypeId = "gold_special",
        ImageUrl = product.ImageUrl,
        Brand = product.Brand,
        Model = product.Model
    };

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

    return View(model);
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

    if (!ModelState.IsValid)
    {
        await PopulateMarketplaceAccountsAsync(input, organizationId);
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
                ["available_quantity"] = variant.Stock,
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

    var client = _httpClientFactory.CreateClient();
    var targetUrl = updatingExistingItem
        ? $"https://api.mercadolibre.com/items/{Uri.EscapeDataString(publication!.ExternalItemId!)}"
        : "https://api.mercadolibre.com/items";

    using var request = new HttpRequestMessage(
        updatingExistingItem ? HttpMethod.Put : HttpMethod.Post,
        targetUrl);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

    request.Content = new StringContent(
        JsonSerializer.Serialize(payload),
        Encoding.UTF8,
        "application/json"
    );

    using var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        input.ErrorMessage = content;
        await PopulateMarketplaceAccountsAsync(input, organizationId);
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

    await _context.SaveChangesAsync();

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

    return RedirectToAction(nameof(Details), new { id = product.Id });
}
private static object[] BuildMercadoLibreAttributes(PublishToMercadoLibreRequest model)
{
    var attributes = new List<object>();

    if (!string.IsNullOrWhiteSpace(model.Brand))
    {
        attributes.Add(new
        {
            id = "BRAND",
            value_name = model.Brand
        });
    }

    if (!string.IsNullOrWhiteSpace(model.Model))
    {
        attributes.Add(new
        {
            id = "MODEL",
            value_name = model.Model
        });
    }

    return attributes.ToArray();
}
public IActionResult CreateFromUrl()
{
    return View(new CreateProductFromUrlViewModel());
}
// GET: /Products
public async Task<IActionResult> Index()
{
    var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();

    var products = await _context.Products
        .Where(x => x.OrganizationId == organizationId)
        .Include(x => x.MarketplacePublications)
        .OrderByDescending(x => x.CreatedAt)
        .ToListAsync();

    return View(products);
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
    publication.ListingTypeId = "gold_special";

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

        Title = extracted.Title,
        Description = extracted.Description,
        SourcePrice = extracted.SourcePrice,
        SourceCurrency = extracted.SourceCurrency,
        SourceStock = extracted.SourceStock,
        ImageUrl = extracted.ImageUrl,
        Brand = extracted.Brand,
        Model = extracted.Model,

        SourceStatus = extracted.SourceStatus,
        LastSourceCheckAt = DateTime.UtcNow,

        Status = "Draft",
        CreatedAt = DateTime.UtcNow,
    };

    _context.Products.Add(product);
    await _context.SaveChangesAsync();

    return RedirectToAction(nameof(Index));
}
    }
}
