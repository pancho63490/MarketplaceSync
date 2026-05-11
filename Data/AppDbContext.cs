using MarketplaceSync.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public DbSet<Product> Products => Set<Product>();
        public DbSet<ProductImage> ProductImages => Set<ProductImage>();
        public DbSet<MercadoLibreToken> MercadoLibreTokens => Set<MercadoLibreToken>();
        public DbSet<ImportLog> ImportLogs => Set<ImportLog>();
    }
}