using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// IMPORTANTE: necesario para IHttpClientFactory
builder.Services.AddHttpClient();

// Servicios de tu app
builder.Services.AddScoped<MarketplaceDetectorService>();
builder.Services.AddScoped<ProductExtractorService>();
builder.Services.AddScoped<EbayApiService>();
builder.Services.AddScoped<MercadoLibreCategoryService>();

builder.Services.AddSession();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseSession();

app.UseAuthorization();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();