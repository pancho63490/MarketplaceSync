using System.Diagnostics;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MarketplaceSync.Web.Services.Tenancy;

namespace MarketplaceSync.Web.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly AppDbContext _context;
        private readonly IOrganizationContext _organizationContext;

        public HomeController(
            ILogger<HomeController> logger,
            AppDbContext context,
            IOrganizationContext organizationContext)
        {
            _logger = logger;
            _context = context;
            _organizationContext = organizationContext;
        }

        public async Task<IActionResult> Index()
        {
            var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync();
            var organizationProducts = _context.Products
                .Where(x => x.OrganizationId == organizationId);

            var products = await organizationProducts
                .Include(x => x.MarketplacePublications)
                .OrderByDescending(x => x.CreatedAt)
                .Take(8)
                .ToListAsync();

            ViewBag.TotalProducts = await organizationProducts.CountAsync();
            ViewBag.EbayProducts = await organizationProducts.CountAsync(x => x.SourceMarketplace == "eBay");
            ViewBag.AmazonProducts = await organizationProducts.CountAsync(x => x.SourceMarketplace == "Amazon");
            ViewBag.MercadoLibreProducts = await organizationProducts.CountAsync(x => x.MarketplacePublications.Any(p => p.Marketplace == "MercadoLibre" && p.IsPublished));
            ViewBag.DraftProducts = await organizationProducts.CountAsync(x => x.Status == "Draft");
            ViewBag.NeedsReviewProducts = await organizationProducts.CountAsync(x => x.Status == "NeedsReview");
            ViewBag.OutOfStockProducts = await organizationProducts.CountAsync(x => x.Status == "OutOfStock" || x.SourceStock == 0);
            ViewBag.MLConnectedAccounts = await _context.MercadoLibreTokens
                .CountAsync(x => x.OrganizationId == organizationId && x.IsActive);
            var organizationOrders = _context.SalesOrders
                .Where(x => x.OrganizationId == organizationId);
            var todayUtc = DateTime.UtcNow.Date;
            ViewBag.TotalOrders = await organizationOrders.CountAsync();
            ViewBag.OrdersToday = await organizationOrders.CountAsync(x => x.DateCreated >= todayUtc);
            ViewBag.PendingOrders = await organizationOrders.CountAsync(x =>
                x.Status != "paid" && x.Status != "cancelled" && x.Status != "invalid");
            ViewBag.TotalSales = await organizationOrders
                .Where(x => x.Status != "cancelled" && x.Status != "invalid")
                .SumAsync(x => (decimal?)x.PaidAmount) ?? 0m;
            var organizationVariants = _context.ProductVariants
                .Where(x => x.OrganizationId == organizationId && x.IsActive);
            ViewBag.LowStockVariants = await organizationVariants.CountAsync(x =>
                x.Stock > 0 && x.Stock <= x.LowStockThreshold);
            ViewBag.OutOfStockVariants = await organizationVariants.CountAsync(x => x.Stock <= 0);

            return View(products);
        }

        [AllowAnonymous]
        public IActionResult Privacy()
        {
            return View();
        }

        [AllowAnonymous]
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
