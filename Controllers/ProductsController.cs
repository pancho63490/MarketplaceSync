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

        public ProductsController(AppDbContext context, MarketplaceDetectorService detector)
        {
            _context = context;
            _detector = detector;
        }

        public async Task<IActionResult> Index()
        {
            var products = await _context.Products
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            return View(products);
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