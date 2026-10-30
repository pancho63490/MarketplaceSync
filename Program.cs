using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Services.Auth;
using MarketplaceSync.Services.Jobs;
using MarketplaceSync.Web.Services.Tenancy;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Hangfire;
using Hangfire.MemoryStorage;

var builder = WebApplication.CreateBuilder(args);
var runMigrations = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);

if (!builder.Environment.IsDevelopment() && !runMigrations)
{
    ValidateProductionConfiguration();
}

var databaseConnectionString = ResolveDatabaseConnectionString();

void ValidateProductionConfiguration()
{
    var requiredSettings = new Dictionary<string, string>
    {
        ["Ebay:ClientId"] = "Ebay__ClientId",
        ["Ebay:ClientSecret"] = "Ebay__ClientSecret",
        ["Ebay:MarketplaceDeletionVerificationToken"] = "Ebay__MarketplaceDeletionVerificationToken",
        ["Importer:Key"] = "Importer__Key",
        ["MercadoLibre:ClientId"] = "MercadoLibre__ClientId",
        ["MercadoLibre:ClientSecret"] = "MercadoLibre__ClientSecret",
        ["MercadoLibre:RedirectUri"] = "MercadoLibre__RedirectUri"
    };

    var missingVariables = requiredSettings
        .Where(setting => string.IsNullOrWhiteSpace(builder.Configuration[setting.Key]))
        .Select(setting => setting.Value)
        .ToArray();

    if (missingVariables.Length > 0)
    {
        throw new InvalidOperationException(
            $"Missing required production environment variables: {string.Join(", ", missingVariables)}.");
    }

    if (builder.Configuration["Importer:Key"]!.Length < 32)
        throw new InvalidOperationException("Importer__Key must contain at least 32 characters.");

    if (builder.Configuration["Ebay:MarketplaceDeletionVerificationToken"]!.Length < 32)
    {
        throw new InvalidOperationException(
            "Ebay__MarketplaceDeletionVerificationToken must contain at least 32 characters.");
    }

    var redirectUriValue = builder.Configuration["MercadoLibre:RedirectUri"]!;
    if (!Uri.TryCreate(redirectUriValue, UriKind.Absolute, out var redirectUri) ||
        redirectUri.Scheme != Uri.UriSchemeHttps)
    {
        throw new InvalidOperationException(
            "MercadoLibre__RedirectUri must be an absolute HTTPS URL outside Development.");
    }
}

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

if (runMigrations)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
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
