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

    var payload = new
    {
        title = input.Title,
        category_id = input.CategoryId,
        price = input.Price,
        currency_id = input.CurrencyId,
        available_quantity = input.Stock,
        buying_mode = "buy_it_now",
        condition = input.Condition,
        listing_type_id = input.ListingTypeId,
        pictures = string.IsNullOrWhiteSpace(input.ImageUrl)
            ? Array.Empty<object>()
            : new object[]
            {
                new { source = input.ImageUrl }
            },
        attributes = BuildMercadoLibreAttributes(input)
    };

    var client = _httpClientFactory.CreateClient();

    var publication = await _context.MarketplacePublications
        .FirstOrDefaultAsync(x => x.ProductId == product.Id &&
                                  x.Marketplace == "MercadoLibre" &&
                                  x.MarketplaceAccountId == token.MarketplaceAccountId);
    var updatingExistingItem = !string.IsNullOrWhiteSpace(publication?.ExternalItemId);
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
