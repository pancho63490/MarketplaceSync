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
        public DbSet<MarketplaceAccount> MarketplaceAccounts { get; set; }

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<OrganizationMembership> OrganizationMemberships { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<MercadoLibreToken> MercadoLibreTokens { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Product>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany(x => x.Products)
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.CreatedByUser)
                    .WithMany()
                    .HasForeignKey(x => x.CreatedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
                entity.HasIndex(x => new
                {
                    x.OrganizationId,
                    x.SourceMarketplace,
                    x.SourceProductId
                });

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

            });

            builder.Entity<MarketplacePublication>(entity =>
            {
                entity.HasOne(x => x.Product)
                    .WithMany(x => x.MarketplacePublications)
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.MarketplaceAccount)
                    .WithMany()
                    .HasForeignKey(x => x.MarketplaceAccountId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(x => x.MarketplaceAccountId);
                entity.HasIndex(x => new
                {
                    x.ProductId,
                    x.Marketplace,
                    x.MarketplaceAccountId
                }).IsUnique()
                    .HasDatabaseName("IX_MarketplacePublications_Product_Channel_Account");

                entity.Property(x => x.Marketplace).HasMaxLength(50);
                entity.Property(x => x.ExternalItemId).HasMaxLength(200);
                entity.Property(x => x.Permalink).HasMaxLength(1000);
                entity.Property(x => x.CurrencyId).HasMaxLength(20);
                entity.Property(x => x.CategoryId).HasMaxLength(100);
                entity.Property(x => x.ListingTypeId).HasMaxLength(50);
                entity.Property(x => x.Condition).HasMaxLength(50);
                entity.Property(x => x.Status).HasMaxLength(100);
            });

            builder.Entity<MercadoLibreToken>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany(x => x.MercadoLibreTokens)
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne<Microsoft.AspNetCore.Identity.IdentityUser>()
                    .WithMany()
                    .HasForeignKey(x => x.ConnectedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(x => x.MarketplaceAccount)
                    .WithMany(x => x.Tokens)
                    .HasForeignKey(x => x.MarketplaceAccountId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(x => new { x.OrganizationId, x.UserId })
                    .IsUnique();

                entity.Property(x => x.UserId).HasMaxLength(200);
                entity.Property(x => x.Nickname).HasMaxLength(300);

                entity.Property(x => x.AccessToken).HasColumnType("text");
                entity.Property(x => x.RefreshToken).HasColumnType("text");

                entity.Property(x => x.TokenType).HasMaxLength(100);
                entity.Property(x => x.Scope).HasColumnType("text");
            });

            builder.Entity<MarketplaceAccount>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany(x => x.MarketplaceAccounts)
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(x => new
                {
                    x.OrganizationId,
                    x.Marketplace,
                    x.ExternalAccountId
                }).IsUnique()
                    .HasDatabaseName("IX_MarketplaceAccounts_Org_Marketplace_ExternalId");

                entity.Property(x => x.Marketplace).HasMaxLength(50);
                entity.Property(x => x.ExternalAccountId).HasMaxLength(200);
                entity.Property(x => x.DisplayName).HasMaxLength(300);
                entity.Property(x => x.Status).HasMaxLength(30);
            });

            builder.Entity<Organization>(entity =>
            {
                entity.HasIndex(x => x.Slug).IsUnique();
                entity.Property(x => x.Name).HasMaxLength(150);
                entity.Property(x => x.Slug).HasMaxLength(180);
            });

            builder.Entity<OrganizationMembership>(entity =>
            {
                entity.HasKey(x => new { x.OrganizationId, x.UserId });
                entity.HasIndex(x => x.UserId);
                entity.Property(x => x.Role).HasMaxLength(30);

                entity.HasOne(x => x.Organization)
                    .WithMany(x => x.Memberships)
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.User)
                    .WithMany()
                    .HasForeignKey(x => x.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
