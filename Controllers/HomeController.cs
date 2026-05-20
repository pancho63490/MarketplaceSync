using System.Diagnostics;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly AppDbContext _context;

        public HomeController(
            ILogger<HomeController> logger,
            AppDbContext context)
        {
            _logger = logger;
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var products = await _context.Products
                .OrderByDescending(x => x.CreatedAt)
                .Take(8)
                .ToListAsync();

            var totalProducts = await _context.Products.CountAsync();

            var ebayProducts = await _context.Products
                .CountAsync(x => x.SourceMarketplace == "eBay");

            var amazonProducts = await _context.Products
                .CountAsync(x => x.SourceMarketplace == "Amazon");

            var mercadoLibreProducts = await _context.Products
                .CountAsync(x => !string.IsNullOrWhiteSpace(x.MercadoLibreItemId));

            var draftProducts = await _context.Products
                .CountAsync(x => x.Status == "Draft");

            var needsReviewProducts = await _context.Products
                .CountAsync(x => x.Status == "NeedsReview");

            var outOfStockProducts = await _context.Products
                .CountAsync(x => x.Status == "OutOfStock" || x.SourceStock == 0);

            var mlConnectedAccounts = await _context.MercadoLibreTokens
                .CountAsync(x => x.IsActive);

            ViewBag.TotalProducts = totalProducts;
            ViewBag.EbayProducts = ebayProducts;
            ViewBag.AmazonProducts = amazonProducts;
            ViewBag.MercadoLibreProducts = mercadoLibreProducts;
            ViewBag.DraftProducts = draftProducts;
            ViewBag.NeedsReviewProducts = needsReviewProducts;
            ViewBag.OutOfStockProducts = outOfStockProducts;
            ViewBag.MLConnectedAccounts = mlConnectedAccounts;

            return View(products);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel
            {
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            });
        }
    }
}