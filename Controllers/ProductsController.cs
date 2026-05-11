using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    private readonly IHttpClientFactory _httpClientFactory;

    public ProductsController(

        AppDbContext context,

        MarketplaceDetectorService detector,

        IHttpClientFactory httpClientFactory)

    {

        _context = context;

        _detector = detector;

        _httpClientFactory = httpClientFactory;

    }
        public async Task<IActionResult> Index()
        {
            var products = await _context.Products
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            return View(products);
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
        pictures = pictures
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
        [HttpGet]
        public IActionResult CreateFromUrl()
        {
            return View(new CreateProductFromUrlViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateFromUrl(CreateProductFromUrlViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var marketplace = _detector.DetectMarketplace(model.SourceUrl);
            var sourceProductId = _detector.ExtractSourceProductId(model.SourceUrl);

            if (marketplace == "UNKNOWN")
            {
                ModelState.AddModelError(nameof(model.SourceUrl), "No se pudo detectar el marketplace. Usa un link de Amazon, eBay o Mercado Libre.");
                return View(model);
            }

            var product = new Product
            {
                SourceUrl = model.SourceUrl,
                SourceMarketplace = marketplace,
                SourceProductId = sourceProductId,
                Status = "DRAFT",
                CreatedAt = DateTime.UtcNow
            };

            _context.Products.Add(product);

        _context.ImportLogs.Add(new ImportLog
{
    ProductId = null,
    SourceUrl = model.SourceUrl,
    Marketplace = marketplace,
    Status = "DRAFT_CREATED",
    Message = $"Producto creado como borrador. SourceProductId: {sourceProductId ?? "N/A"}",
    CreatedAt = DateTime.UtcNow
});

            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }
    }
    
    
}