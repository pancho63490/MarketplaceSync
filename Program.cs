using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Services.Auth;
using MarketplaceSync.Services.Jobs;
using MarketplaceSync.Services.Inventory;
using MarketplaceSync.Services.MarketInsights;
using MarketplaceSync.Services.CrossBorder;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Monitor;
using MarketplaceSync.Services.Purchasing;
using MarketplaceSync.Web.Services.Tenancy;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Hangfire;
using Hangfire.MemoryStorage;

var builder = WebApplication.CreateBuilder(args);
var migrateOnly = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
var migrateAndRun = args.Contains("--migrate-and-run", StringComparer.OrdinalIgnoreCase);

var databaseConnectionString = ResolveDatabaseConnectionString();

string ResolveDatabaseConnectionString()
{
    var explicitConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
    if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        return explicitConnectionString;

    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrWhiteSpace(databaseUrl))
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        {
            throw new InvalidOperationException("DATABASE_URL must be a PostgreSQL URL.");
        }

        var credentials = uri.UserInfo.Split(':', 2);
        if (credentials.Length != 2 || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
            throw new InvalidOperationException("DATABASE_URL must include a database name and credentials.");

        var parsedConnectionString = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = Uri.UnescapeDataString(credentials[1])
        };

        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]).Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                parsedConnectionString.SslMode = Enum.Parse<Npgsql.SslMode>(Uri.UnescapeDataString(pair[1]), true);
        }

        return parsedConnectionString.ConnectionString;
    }

    var configuredConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(configuredConnectionString))
        throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection or DATABASE_URL.");

    return configuredConnectionString;
}

var renderPort = Environment.GetEnvironmentVariable("PORT");
if (int.TryParse(renderPort, out var port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(databaseConnectionString));

builder.Services.AddIdentity<IdentityUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";

    options.Cookie.Name = "MarketplaceSync.Auth";

    options.ExpireTimeSpan = TimeSpan.FromHours(8);

    options.SlidingExpiration = true;
});

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("ScraperClient", client =>
{
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "User-Agent",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IOrganizationContext, OrganizationContext>();

builder.Services.AddScoped<IMercadoLibreService, MercadoLibreService>();

builder.Services.AddScoped<
    IMercadoLibreOrderSyncService,
    MercadoLibreOrderSyncService>();

builder.Services.AddScoped<
    IMercadoLibreAuthService,
    MercadoLibreAuthService>();

builder.Services.AddScoped<MarketplaceJobsService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IMercadoLibreBestSellerService, MercadoLibreBestSellerService>();
builder.Services.AddScoped<IMercadoLibreFeeService, MercadoLibreFeeService>();
builder.Services.AddSingleton<IMarketOpportunityAnalyzer, MarketOpportunityAnalyzer>();
builder.Services.AddScoped<IMarketRankingHistoryService, MarketRankingHistoryService>();
builder.Services.AddScoped<ICrossBorderPricingService, CrossBorderPricingService>();
builder.Services.AddHttpClient(ClaudeClient.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<ClaudeClient>();
builder.Services.AddScoped<IProductAiEnricherService, ClaudeProductAiEnricherService>();
builder.Services.AddScoped<MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient>();
builder.Services.AddScoped<IListingAutopilotService, ListingAutopilotService>();
builder.Services.AddScoped<MarketplaceSync.Services.MercadoLibre.MercadoLibreItemEditorService>();
builder.Services.AddScoped<MarketplaceSync.Services.MercadoLibre.IMercadoLibreListingSyncService, MarketplaceSync.Services.MercadoLibre.MercadoLibreListingSyncService>();
builder.Services.AddScoped<MarketplaceSync.Services.MercadoLibre.IStockSyncScheduler, MarketplaceSync.Services.MercadoLibre.StockSyncScheduler>();
builder.Services.AddScoped<MarketplaceSync.Services.Ai.ProductTitleTranslator>();
builder.Services.AddScoped<MarketplaceSync.Services.Inventory.InventoryLotService>();
builder.Services.AddScoped<MarketplaceSync.Services.MarketInsights.CategoryDiscoveryService>();
builder.Services.AddScoped<MarketplaceSync.Services.Purchasing.RadarAutoPrepareService>();
builder.Services.AddHttpClient(MarketplaceSync.Services.Notifications.TelegramClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<MarketplaceSync.Services.Notifications.TelegramClient>();
builder.Services.AddScoped<MarketplaceSync.Services.Notifications.TelegramNotificationService>();
builder.Services.AddScoped<MarketplaceSync.Services.MercadoLibre.IMercadoLibreQuestionService, MarketplaceSync.Services.MercadoLibre.MercadoLibreQuestionService>();

builder.Services.AddScoped<ISourcePriceMonitorService, SourcePriceMonitorService>();
builder.Services.AddScoped<SourceSearchQueryBuilder>();
builder.Services.AddScoped<IPurchaseRadarService, PurchaseRadarService>();

builder.Services.AddScoped<MarketplaceDetectorService>();

builder.Services.AddScoped<ProductExtractorService>();

builder.Services.AddScoped<EbayApiService>();

builder.Services.AddScoped<MercadoLibreCategoryService>();

builder.Services.AddHangfire(config =>
{
    if (builder.Environment.IsDevelopment())
    {
        config.UseMemoryStorage();
    }
    else
    {
        config.UsePostgreSqlStorage(options =>
            options.UseNpgsqlConnection(databaseConnectionString));
    }
});

builder.Services.AddHangfireServer();
builder.Services.AddHostedService<HangfireRecurringJobsRegistrationService>();

builder.Services.AddControllersWithViews();

builder.Services.AddSession();

var app = builder.Build();

if (migrateOnly || migrateAndRun)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();

    if (migrateOnly)
        return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");

    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseSession();

app.UseAuthentication();

app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseHangfireDashboard("/hangfire");
}

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
