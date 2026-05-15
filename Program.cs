using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore;
using MarketplaceSync.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null
            );
        }
    )
);

builder.Services.AddHttpClient();

builder.Services.AddScoped<MarketplaceDetectorService>();
builder.Services.AddScoped<ProductExtractorService>();
builder.Services.AddScoped<EbayApiService>();
builder.Services.AddScoped<MercadoLibreCategoryService>();
builder.Services.AddControllersWithViews();

var app = builder.Build();

// TEMPORALMENTE DESACTIVADO PARA EVITAR QUE RENDER SE CAIGA AL ARRANCAR
// try
// {
//     using var scope = app.Services.CreateScope();
//     var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//     dbContext.Database.Migrate();
// }
// catch (Exception ex)
// {
//     Console.WriteLine("ERROR APPLYING MIGRATIONS:");
//     Console.WriteLine(ex.ToString());
//     throw;
// }

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

// API Controllers: /api/import/product
app.MapControllers();

// MVC Controllers: /Products, /MercadoLibre, /Home, etc.
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();