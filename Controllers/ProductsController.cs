using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceSync.Web.Services;
using System.Text.Json.Nodes;

using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Services;
using MarketplaceSync.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers
{
    public class ProductsController : Controller
    {
          private readonly AppDbContext _context;

    private readonly MarketplaceDetectorService _detector;
private readonly ProductExtractorService _extractor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MercadoLibreCategoryService _mercadoLibreCategoryService;

public ProductsController(
    AppDbContext context,
    MarketplaceDetectorService detector,
    ProductExtractorService extractor,
    IHttpClientFactory httpClientFactory,
    MercadoLibreCategoryService mercadoLibreCategoryService
)
{
    _context = context;
    _detector = detector;
    _extractor = extractor;
    _httpClientFactory = httpClientFactory;
    _mercadoLibreCategoryService = mercadoLibreCategoryService;
}
        
        private async Task<List<MercadoLibreAttributeInput>> GetRequiredAttributesAsync(string categoryId)
{
    var result = new List<MercadoLibreAttributeInput>();

    var token = await _context.MercadoLibreTokens
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

    request.Attributes = await GetRequiredAttributesAsync(request.CategoryId);

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
    var product = await _context.Products.FindAsync(id);

    if (product == null)
        return NotFound();

    if (string.IsNullOrWhiteSpace(mercadoLibreCategoryId))
    {
        TempData["Error"] = "Selecciona una categoría válida.";
        return RedirectToAction(nameof(PublishToMercadoLibre), new { id });
    }

    product.MercadoLibreCategoryId = mercadoLibreCategoryId.Trim();
    product.UpdatedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync();

    TempData["Success"] = $"Categoría Mercado Libre guardada: {product.MercadoLibreCategoryId}";

    return RedirectToAction(nameof(PublishToMercadoLibre), new { id });
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SuggestMercadoLibreCategory(int id)
{
    var product = await _context.Products.FindAsync(id);

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
    var product = await _context.Products.FindAsync(id);

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
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id);

    if (product == null)
    {
        TempData["Error"] = "Producto no encontrado.";
        return RedirectToAction(nameof(Index));
    }

    if (string.IsNullOrWhiteSpace(product.SourceUrl))
    {
        TempData["Error"] = "El producto no tiene URL de origen para actualizar.";
        return RedirectToAction(nameof(Edit), new { id = product.Id });
    }

    try
    {
        var extracted = await _extractor.ExtractAsync(product.SourceUrl.Trim());

        product.SourceMarketplace = !string.IsNullOrWhiteSpace(extracted.SourceMarketplace)
            ? extracted.SourceMarketplace
            : product.SourceMarketplace;

        product.SourceProductId = !string.IsNullOrWhiteSpace(extracted.SourceProductId)
            ? extracted.SourceProductId
            : product.SourceProductId;

        product.Title = !string.IsNullOrWhiteSpace(extracted.Title)
            ? extracted.Title
            : product.Title;

        product.Description = !string.IsNullOrWhiteSpace(extracted.Description)
            ? extracted.Description
            : product.Description;

        product.SourcePrice = extracted.SourcePrice ?? product.SourcePrice;

        product.SourceCurrency = !string.IsNullOrWhiteSpace(extracted.SourceCurrency)
            ? extracted.SourceCurrency
            : product.SourceCurrency;

        product.SourceStock = extracted.SourceStock ?? product.SourceStock;

        product.SourceAvailabilityText = !string.IsNullOrWhiteSpace(extracted.SourceAvailabilityText)
            ? extracted.SourceAvailabilityText
            : product.SourceAvailabilityText;

        product.ImageUrl = !string.IsNullOrWhiteSpace(extracted.ImageUrl)
            ? extracted.ImageUrl
            : product.ImageUrl;

        product.Brand = !string.IsNullOrWhiteSpace(extracted.Brand)
            ? extracted.Brand
            : product.Brand;

        product.Model = !string.IsNullOrWhiteSpace(extracted.Model)
            ? extracted.Model
            : product.Model;

        product.SourceStatus = !string.IsNullOrWhiteSpace(extracted.SourceStatus)
            ? extracted.SourceStatus
            : "Updated";

        product.LastSourceCheckAt = DateTime.UtcNow;
        product.UpdatedAt = DateTime.UtcNow;

        if (product.SourceStock.HasValue && product.SourceStock.Value <= 0)
        {
            product.Status = "OutOfStock";
        }
        else
        {
            product.Status = "NeedsReview";
        }

        await _context.SaveChangesAsync();

        TempData["Success"] = "Información del producto actualizada correctamente.";
    }
    catch (Exception ex)
    {
        TempData["Error"] = $"Error al actualizar información del producto: {ex.Message}";
    }

    return RedirectToAction(nameof(Edit), new { id = product.Id });
}
// GET: /Products/Delete/5
[HttpGet]
public async Task<IActionResult> Delete(int id)
{
    var product = await _context.Products
        .FirstOrDefaultAsync(p => p.Id == id);

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
    var products = await _context.Products
        .Where(x => !string.IsNullOrWhiteSpace(x.SourceUrl))
        .OrderByDescending(x => x.CreatedAt)
        .ToListAsync();

    if (!products.Any())
    {
        TempData["Error"] = "No hay productos para actualizar.";
        return RedirectToAction(nameof(Index));
    }

    var updated = 0;
    var failed = 0;

    foreach (var product in products)
    {
        try
        {
            var extracted = await _extractor.ExtractAsync(product.SourceUrl);

            product.SourceMarketplace = extracted.SourceMarketplace;
            product.SourceProductId = extracted.SourceProductId;
            product.Title = extracted.Title;
            product.Description = extracted.Description;
            product.SourcePrice = extracted.SourcePrice;
            product.SourceCurrency = extracted.SourceCurrency;
            product.SourceStock = extracted.SourceStock;
            product.SourceAvailabilityText = extracted.SourceAvailabilityText;
            product.ImageUrl = extracted.ImageUrl;
            product.Brand = extracted.Brand;
            product.Model = extracted.Model;
            product.SourceStatus = extracted.SourceStatus;
            product.LastSourceCheckAt = DateTime.UtcNow;
            product.UpdatedAt = DateTime.UtcNow;

            updated++;
        }
        catch
        {
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
    var product = await _context.Products
        .FirstOrDefaultAsync(p => p.Id == id);

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
{
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id);

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
    var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == id);

    if (product == null)
        return NotFound();

    var model = new PublishToMercadoLibreRequest
    {
        ProductId = product.Id,
        Title = !string.IsNullOrWhiteSpace(product.Title)
            ? product.Title.Length > 60 ? product.Title.Substring(0, 60) : product.Title
            : "Producto sin título",

        Description = product.Description,
        Price = product.MercadoLibrePrice ?? product.SourcePrice ?? 0,
        Stock = product.MercadoLibreStock ?? product.SourceStock ?? 1,
        CurrencyId = product.MercadoLibreCurrencyId ?? "MXN",
        CategoryId = product.MercadoLibreCategoryId ?? "",
        Condition = product.MercadoLibreCondition ?? "new",
        ListingTypeId = product.MercadoLibreListingTypeId ?? "gold_special",
        ImageUrl = product.ImageUrl,
        Brand = product.Brand,
        Model = product.Model
    };

    return View(model);
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> PublishToMercadoLibre(PublishToMercadoLibreRequest model)
{
    var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == model.ProductId);

    if (product == null)
        return NotFound();

    if (!ModelState.IsValid)
        return View(model);

    var token = await _context.MercadoLibreTokens
        .OrderByDescending(x => x.UpdatedAt)
        .FirstOrDefaultAsync();

    if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
    {
        model.ErrorMessage = "No hay token activo de Mercado Libre. Conecta primero la cuenta.";
        return View(model);
    }

    var payload = new
    {
        title = model.Title,
        category_id = model.CategoryId,
        price = model.Price,
        currency_id = model.CurrencyId,
        available_quantity = model.Stock,
        buying_mode = "buy_it_now",
        condition = model.Condition,
        listing_type_id = model.ListingTypeId,
        pictures = string.IsNullOrWhiteSpace(model.ImageUrl)
            ? Array.Empty<object>()
            : new object[]
            {
                new { source = model.ImageUrl }
            },
        attributes = BuildMercadoLibreAttributes(model)
    };

    var client = _httpClientFactory.CreateClient();

    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadolibre.com/items");
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
        model.ErrorMessage = content;
        return View(model);
    }

    using var document = JsonDocument.Parse(content);
    var root = document.RootElement;

    product.MercadoLibreItemId = root.TryGetProperty("id", out var idElement)
        ? idElement.GetString()
        : null;

    product.MercadoLibrePermalink = root.TryGetProperty("permalink", out var permalinkElement)
        ? permalinkElement.GetString()
        : null;

    product.MercadoLibreStatus = root.TryGetProperty("status", out var statusElement)
        ? statusElement.GetString()
        : "published";

    product.MercadoLibreCategoryId = model.CategoryId;
    product.MercadoLibrePrice = model.Price;
    product.MercadoLibreStock = model.Stock;
    product.MercadoLibreCurrencyId = model.CurrencyId;
    product.MercadoLibreListingTypeId = model.ListingTypeId;
    product.MercadoLibreCondition = model.Condition;
    product.MercadoLibrePublishedAt = DateTime.UtcNow;
    product.Status = "Published";
    product.UpdatedAt = DateTime.UtcNow;

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
    var products = await _context.Products
        .OrderByDescending(x => x.CreatedAt)
        .ToListAsync();

    return View(products);
}

// GET: /Products/Edit/5
[HttpGet]
public async Task<IActionResult> Edit(int id)
{
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id);

    if (product == null)
    {
        return NotFound();
    }

    return View(product);
}

// POST: /Products/Edit/5
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Edit(int id, Product model)
{
    if (id != model.Id)
    {
        return BadRequest();
    }

    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id);

    if (product == null)
    {
        return NotFound();
    }

    if (!ModelState.IsValid)
    {
        return View(model);
    }

    product.Title = model.Title;
    product.Description = model.Description;
    product.Brand = model.Brand;
    product.Model = model.Model;
    product.ImageUrl = model.ImageUrl;

    product.SourcePrice = model.SourcePrice;
    product.SourceCurrency = model.SourceCurrency;
    product.SourceStock = model.SourceStock;
    product.SourceStatus = model.SourceStatus;
    product.SourceAvailabilityText = model.SourceAvailabilityText;

    product.MercadoLibreCategoryId = model.MercadoLibreCategoryId;
    product.MercadoLibrePrice = model.MercadoLibrePrice;
    product.MercadoLibreStock = model.MercadoLibreStock;
    product.MercadoLibreCurrencyId = model.MercadoLibreCurrencyId;
    product.MercadoLibreListingTypeId = model.MercadoLibreListingTypeId;
    product.MercadoLibreCondition = model.MercadoLibreCondition;

    product.Status = model.Status;
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
    var product = await _context.Products
        .FirstOrDefaultAsync(x => x.Id == id);

    if (product == null)
    {
        return NotFound();
    }

    product.MercadoLibreCurrencyId = "MXN";
    product.MercadoLibreCondition = "new";
    product.MercadoLibreListingTypeId = "gold_special";

    if (product.SourceStock.HasValue)
    {
        product.MercadoLibreStock = product.SourceStock.Value;
    }

    if (product.SourcePrice.HasValue)
    {
        if (string.Equals(product.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            // Temporal: tipo de cambio fijo + margen
            product.MercadoLibrePrice = Math.Round(product.SourcePrice.Value * 18.50m * 1.25m, 2);
        }
        else
        {
            // Si ya viene en MXN, solo agrega margen
            product.MercadoLibrePrice = Math.Round(product.SourcePrice.Value * 1.25m, 2);
        }
    }

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

    var product = new Product
    {
        SourceUrl = extracted.SourceUrl,
        SourceMarketplace = extracted.SourceMarketplace,
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
        CreatedAt = DateTime.UtcNow
    };

    _context.Products.Add(product);
    await _context.SaveChangesAsync();

    return RedirectToAction(nameof(Index));
}
    }
}