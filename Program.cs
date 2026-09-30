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

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));

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
            options.UseNpgsqlConnection(
                builder.Configuration.GetConnectionString("DefaultConnection")));
    }
});

builder.Services.AddHangfireServer();

builder.Services.AddControllersWithViews();

builder.Services.AddSession();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var recurringJobManager =
        scope.ServiceProvider
            .GetRequiredService<IRecurringJobManager>();

    recurringJobManager.AddOrUpdate<MarketplaceJobsService>(
        "refresh-mercadolibre-token",
        x => x.RefreshMercadoLibreToken(),
        "*/30 * * * *");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");

    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseHangfireDashboard("/hangfire");

app.UseRouting();

app.UseSession();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
