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
        private readonly MarketplaceSync.Services.Metrics.MetricsService _metrics;

        public HomeController(
            ILogger<HomeController> logger,
            AppDbContext context,
            IOrganizationContext organizationContext,
            MarketplaceSync.Services.Metrics.MetricsService metrics)
        {
            _metrics = metrics;
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
            // "Hoy" es el día de Ciudad Juárez (UTC-7), no el de UTC.
            var todayLocal = DateTime.UtcNow.AddHours(-7).Date;
            var todayUtc = DateTime.SpecifyKind(todayLocal.AddHours(7), DateTimeKind.Utc);
            var yesterdayUtc = todayUtc.AddDays(-1);
            var sameTimeYesterdayUtc = DateTime.UtcNow.AddDays(-1);
            ViewBag.TotalOrders = await organizationOrders.CountAsync();
            var paidToday = organizationOrders.Where(x => x.Status == "paid" && x.DateCreated >= todayUtc);
            ViewBag.OrdersToday = await paidToday.CountAsync();
            ViewBag.SalesToday = await paidToday.SumAsync(x => (decimal?)x.TotalAmount) ?? 0m;
            ViewBag.SalesYesterdaySoFar = await organizationOrders
                .Where(x => x.Status == "paid" && x.DateCreated >= yesterdayUtc && x.DateCreated < sameTimeYesterdayUtc)
                .SumAsync(x => (decimal?)x.TotalAmount) ?? 0m;
            ViewBag.OrdersToShip = await MarketplaceSync.Web.Models.Orders.SalesOrderShippingQueries.ToShip(organizationOrders).CountAsync();
            ViewBag.LateOrders = await MarketplaceSync.Web.Models.Orders.SalesOrderShippingQueries.LateToShip(organizationOrders, DateTime.UtcNow).CountAsync();
            ViewBag.OpenClaims = await _context.OrderClaims.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && c.Status == "opened")
                .OrderBy(c => c.DueDate ?? DateTime.MaxValue)
                .ToListAsync();
            ViewBag.UnansweredMessages = (await HttpContext.RequestServices
                .GetRequiredService<MarketplaceSync.Services.PostSale.PostSaleService>()
                .GetPendingAsync(organizationId, 99, HttpContext.RequestAborted)).Count;
            try
            {
                ViewBag.ReputationAlerts = await _metrics.GetReputationAlertsAsync(organizationId, HttpContext.RequestAborted);
            }
            catch (Exception exception) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Reputation alerts unavailable on Hoy.");
                ViewBag.ReputationAlerts = Array.Empty<MarketplaceSync.Services.Metrics.ReputationAlert>();
            }
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

            var candidates = _context.PurchaseCandidates.Where(x => x.OrganizationId == organizationId);
            ViewBag.RadarToBuy = await candidates.CountAsync(x =>
                x.Status == MarketplaceSync.Web.Models.Purchasing.PurchaseCandidateStatus.Suggested && x.DemandScore > 0);
            ViewBag.RadarInTransit = await candidates.CountAsync(x =>
                x.Status == MarketplaceSync.Web.Models.Purchasing.PurchaseCandidateStatus.Purchased);
            ViewBag.OrganizationName = await _context.Organizations
                .Where(x => x.Id == organizationId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync();
            var since30 = DateTime.UtcNow.AddDays(-30);
            ViewBag.Sales30Days = await organizationOrders
                .Where(x => x.DateCreated >= since30 && x.Status != "cancelled" && x.Status != "invalid")
                .SumAsync(x => (decimal?)x.PaidAmount) ?? 0m;
            ViewBag.Orders30Days = await organizationOrders
                .CountAsync(x => x.DateCreated >= since30 && x.Status != "cancelled" && x.Status != "invalid");
            ViewBag.UnitsInStock = await organizationVariants.SumAsync(x => (int?)(x.Stock > 0 ? x.Stock : 0)) ?? 0;
            var expiryLimit = MarketplaceSync.Services.Inventory.InventoryLotService.Today.AddDays(MarketplaceSync.Services.Inventory.InventoryLotService.WarningDays);
            ViewBag.ExpiringLots = await _context.InventoryLots.CountAsync(x =>
                x.OrganizationId == organizationId && x.WrittenOffAt == null && x.QuantityRemaining > 0 && x.ExpiresOn <= expiryLimit);
            ViewBag.TelegramLinked = await _context.NotificationSettings.AnyAsync(x =>
                x.OrganizationId == organizationId && x.TelegramChatId != null);
            ViewBag.WatchedCategories = await _context.WatchedCategories.CountAsync(x =>
                x.OrganizationId == organizationId && x.IsSelected);
            ViewBag.OpenQuestions = await _context.BuyerQuestions.CountAsync(x =>
                x.OrganizationId == organizationId && x.Status == MarketplaceSync.Web.Models.Questions.BuyerQuestionStatus.Unanswered);
            ViewBag.ReadyProducts = await organizationProducts.CountAsync(x =>
                x.Status == "ReadyToPublish" &&
                !x.MarketplacePublications.Any(p => p.Marketplace == "MercadoLibre" && p.IsPublished));

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
