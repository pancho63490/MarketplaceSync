using MarketplaceSync.Web.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MarketplaceSync.Web.Models.Marketplace;
namespace MarketplaceSync.Web.Data
{
    public class AppDbContext : IdentityDbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }
        public DbSet<MarketplacePublication>
    MarketplacePublications { get; set; }

        public DbSet<Product> Products { get; set; }
        public DbSet<MercadoLibreToken> MercadoLibreTokens { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Product>(entity =>
            {
                entity.Property(x => x.SourceUrl).HasMaxLength(1000);
                entity.Property(x => x.SourceMarketplace).HasMaxLength(100);
                entity.Property(x => x.SourceProductId).HasMaxLength(200);
                entity.Property(x => x.Title).HasMaxLength(300);
                entity.Property(x => x.SourceCurrency).HasMaxLength(20);
                entity.Property(x => x.ImageUrl).HasMaxLength(1000);
                entity.Property(x => x.Brand).HasMaxLength(200);
                entity.Property(x => x.Model).HasMaxLength(200);
                entity.Property(x => x.SourceStatus).HasMaxLength(100);
                entity.Property(x => x.Status).HasMaxLength(100);

                entity.Property(x => x.MercadoLibreItemId).HasMaxLength(100);
                entity.Property(x => x.MercadoLibreCategoryId).HasMaxLength(100);
                entity.Property(x => x.MercadoLibreCurrencyId).HasMaxLength(20);
                entity.Property(x => x.MercadoLibreListingTypeId).HasMaxLength(100);
                entity.Property(x => x.MercadoLibreCondition).HasMaxLength(50);
                entity.Property(x => x.MercadoLibreStatus).HasMaxLength(100);
                entity.Property(x => x.MercadoLibrePermalink).HasMaxLength(1000);
            });

            builder.Entity<MercadoLibreToken>(entity =>
            {
                entity.Property(x => x.UserId).HasMaxLength(200);
                entity.Property(x => x.Nickname).HasMaxLength(300);

                entity.Property(x => x.AccessToken).HasColumnType("text");
                entity.Property(x => x.RefreshToken).HasColumnType("text");

                entity.Property(x => x.TokenType).HasMaxLength(100);
                entity.Property(x => x.Scope).HasColumnType("text");
            });
        }
    }
}