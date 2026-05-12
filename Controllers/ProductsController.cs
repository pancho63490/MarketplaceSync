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

 public ProductsController(
    AppDbContext context,
    MarketplaceDetectorService detector,
    ProductExtractorService extractor,
    IHttpClientFactory httpClientFactory)
{
    _context = context;
    _detector = detector;
    _extractor = extractor;
    _httpClientFactory = httpClientFactory;
}
        public async Task<IActionResult> Index()
        {
            var products = await _context.Products
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            return View(products);
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
[HttpGet]
public async Task<IActionResult> PublishToMercadoLibre(int id)
{
    var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == id);

    if (product == null)
        return NotFound();

    var request = new PublishToMercadoLibreRequest
    {
        ProductId = product.Id,
        Title = product.Title ?? string.Empty,
        Price = product.MercadoLibrePrice ?? 1,
        Stock = product.MercadoLibreStock ?? 1,
        CurrencyId = product.MercadoLibreCurrencyId ?? "MXN",
        ListingTypeId = product.MercadoLibreListingTypeId ?? "gold_special",
        Condition = product.MercadoLibreCondition ?? "new",
        CategoryId = product.MercadoLibreCategoryId ?? string.Empty,
        PictureUrl = null
    };

    return View(request);
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> PublishToMercadoLibre(PublishToMercadoLibreRequest request)
{
    if (!ModelState.IsValid)
        return View(request);

    var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == request.ProductId);

    if (product == null)
        return NotFound();

    var token = await _context.MercadoLibreTokens
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefaultAsync();

    if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
    {
        ModelState.AddModelError("", "Primero conecta tu cuenta de Mercado Libre.");
        return View(request);
    }

    var pictures = new List<object>();

    if (!string.IsNullOrWhiteSpace(request.PictureUrl))
    {
        pictures.Add(new
        {
            source = request.PictureUrl
        });
    }
var attributes = request.Attributes
    .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.ValueName))
    .Select(x => new
    {
        id = x.Id,
        value_name = x.ValueName
    })
    .Cast<object>()
    .ToList();

var payload = new
{
    title = request.Title,
    category_id = request.CategoryId,
    price = request.Price,
    currency_id = request.CurrencyId,
    available_quantity = request.Stock,
    buying_mode = "buy_it_now",
    listing_type_id = request.ListingTypeId,
    condition = request.Condition,
    pictures = pictures,
    attributes = attributes
};

    var client = _httpClientFactory.CreateClient();

    var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadolibre.com/items");
    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    httpRequest.Content = new StringContent(
        JsonSerializer.Serialize(payload),
        Encoding.UTF8,
        "application/json");

    var response = await client.SendAsync(httpRequest);
    var content = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        ModelState.AddModelError("", content);
        return View(request);
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

    product.MercadoLibreCategoryId = request.CategoryId;
    product.MercadoLibrePrice = request.Price;
    product.MercadoLibreStock = request.Stock;
    product.MercadoLibreCurrencyId = request.CurrencyId;
    product.MercadoLibreListingTypeId = request.ListingTypeId;
    product.MercadoLibreCondition = request.Condition;
    product.MercadoLibrePublishedAt = DateTime.UtcNow;
    product.Status = "Published";
    product.UpdatedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync();

    return RedirectToAction(nameof(Index));
}
     
public IActionResult CreateFromUrl()
{
    return View(new CreateProductFromUrlViewModel());
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