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
            // Serie diaria (día de Juárez) de los últimos 30 días y el total de los 30 anteriores, para la gráfica de Hoy.
            var startLocal = todayLocal.AddDays(-29);
            var startUtc = DateTime.SpecifyKind(startLocal.AddHours(7), DateTimeKind.Utc);
            var recent = await organizationOrders
                .Where(x => x.DateCreated >= startUtc && x.Status != "cancelled" && x.Status != "invalid")
                .Select(x => new { x.DateCreated, x.PaidAmount })
                .ToListAsync();
            var byDay = recent.GroupBy(x => x.DateCreated.AddHours(-7).Date).ToDictionary(g => g.Key, g => g.Sum(x => x.PaidAmount));
            ViewBag.SalesDaily = Enumerable.Range(0, 30).Select(i => startLocal.AddDays(i))
                .Select(d => (Day: d, Total: byDay.GetValueOrDefault(d))).ToList();
            var prevStartUtc = startUtc.AddDays(-30);
            ViewBag.SalesPrev30 = await organizationOrders
                .Where(x => x.DateCreated >= prevStartUtc && x.DateCreated < startUtc && x.Status != "cancelled" && x.Status != "invalid")
                .SumAsync(x => (decimal?)x.PaidAmount) ?? 0m;
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
            ViewBag.CatalogLosing = await _context.MarketplacePublications.CountAsync(x =>
                x.Product.OrganizationId == organizationId && x.CatalogListing && x.Status == "active" && x.CatalogStatus == "competing");
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
            var model = new ErrorViewModel
            {
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            };

            // Al dueño de la cuenta se le muestra qué falló para poder corregirlo; a visitantes sin sesión, nada.
            var feature = HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
            if (feature?.Error is { } error && User.Identity?.IsAuthenticated == true)
            {
                var root = error;
                while (root.InnerException is not null)
                    root = root.InnerException;
                model.Path = feature.Path;
                model.Detail = root == error
                    ? $"{error.GetType().Name}: {error.Message}"
                    : $"{error.GetType().Name}: {error.Message} → {root.GetType().Name}: {root.Message}";
                if (model.Detail.Length > 1200)
                    model.Detail = model.Detail[..1200];
                model.Where = string.Join("\n", (error.StackTrace ?? string.Empty)
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Contains("MarketplaceSync", StringComparison.Ordinal))
                    .Take(4));
            }

            return View(model);
        }
    }
}
