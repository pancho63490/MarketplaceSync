using MarketplaceSync.Web.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Models.MarketInsights;
using MarketplaceSync.Web.Models.CrossBorder;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Models.Questions;
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
        public DbSet<ProductVariant> ProductVariants { get; set; }
        public DbSet<MarketplacePublicationVariant> MarketplacePublicationVariants { get; set; }
        public DbSet<MercadoLibreToken> MercadoLibreTokens { get; set; }
        public DbSet<SalesOrder> SalesOrders { get; set; }
        public DbSet<SalesOrderItem> SalesOrderItems { get; set; }
        public DbSet<InventoryMovement> InventoryMovements { get; set; }
        public DbSet<MarketRankingSnapshot> MarketRankingSnapshots { get; set; }
        public DbSet<MarketRankingSnapshotItem> MarketRankingSnapshotItems { get; set; }
        public DbSet<CrossBorderSettings> CrossBorderSettings { get; set; }
        public DbSet<PurchaseCandidate> PurchaseCandidates { get; set; }
        public DbSet<BuyerQuestion> BuyerQuestions { get; set; }
        public DbSet<MarketplaceSync.Web.Models.Inventory.InventoryLot> InventoryLots { get; set; }

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
                entity.Property(x => x.AttributesJson).HasColumnType("jsonb");
                entity.Property(x => x.SaleFeeMxn).HasColumnType("numeric(18,2)");
                entity.Property(x => x.SaleFeeForPrice).HasColumnType("numeric(18,2)");
                entity.HasIndex(x => x.ExternalItemId);
            });

            builder.Entity<ProductVariant>(entity =>
            {
                entity.HasOne(x => x.Product)
                    .WithMany(x => x.Variants)
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.Organization)
                    .WithMany()
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(x => new { x.OrganizationId, x.Sku }).IsUnique();
                entity.HasIndex(x => new { x.ProductId, x.IsActive });
                entity.Property(x => x.Sku).HasMaxLength(100);
                entity.Property(x => x.Barcode).HasMaxLength(100);
                entity.Property(x => x.AttributesJson).HasColumnType("jsonb");
                entity.Property(x => x.Price).HasColumnType("numeric(18,2)");
                entity.Property(x => x.LowStockThreshold).HasDefaultValue(5);
            });

            builder.Entity<MarketplacePublicationVariant>(entity =>
            {
                entity.HasOne(x => x.MarketplacePublication)
                    .WithMany(x => x.Variants)
                    .HasForeignKey(x => x.MarketplacePublicationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.ProductVariant)
                    .WithMany(x => x.MarketplacePublications)
                    .HasForeignKey(x => x.ProductVariantId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(x => new { x.MarketplacePublicationId, x.ProductVariantId }).IsUnique();
                entity.Property(x => x.ExternalVariationId).HasMaxLength(100);
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
                entity.Property(x => x.LastOrdersSyncError).HasMaxLength(1000);
            });

            builder.Entity<MarketRankingSnapshot>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany()
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.MarketplaceAccount)
                    .WithMany()
                    .HasForeignKey(x => x.MarketplaceAccountId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new
                {
                    x.OrganizationId,
                    x.CategoryId,
                    x.SnapshotDate
                }).IsUnique().HasDatabaseName("IX_MarketRankingSnapshots_Daily");
                entity.HasIndex(x => new { x.OrganizationId, x.CategoryId, x.CapturedAt });
                entity.Property(x => x.CategoryId).HasMaxLength(100);
                entity.Property(x => x.CategoryName).HasMaxLength(300);
                entity.Property(x => x.SnapshotDate).HasColumnType("date");
            });

            builder.Entity<MarketRankingSnapshotItem>(entity =>
            {
                entity.HasOne(x => x.MarketRankingSnapshot)
                    .WithMany(x => x.Items)
                    .HasForeignKey(x => x.MarketRankingSnapshotId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => new { x.MarketRankingSnapshotId, x.ExternalId }).IsUnique();
                entity.HasIndex(x => x.ExternalId);
                entity.Property(x => x.ExternalId).HasMaxLength(200);
                entity.Property(x => x.Type).HasMaxLength(30);
                entity.Property(x => x.Name).HasMaxLength(500);
                entity.Property(x => x.Brand).HasMaxLength(200);
                entity.Property(x => x.Model).HasMaxLength(200);
                entity.Property(x => x.ImageUrl).HasMaxLength(1000);
                entity.Property(x => x.Price).HasColumnType("numeric(18,2)");
                entity.Property(x => x.CurrencyId).HasMaxLength(20);
                entity.Property(x => x.Permalink).HasMaxLength(1000);
            });

            builder.Entity<CrossBorderSettings>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany()
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => x.OrganizationId).IsUnique();
            });

            builder.Entity<PurchaseCandidate>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany()
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(x => x.MarketplaceAccount)
                    .WithMany()
                    .HasForeignKey(x => x.MarketplaceAccountId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(x => new { x.OrganizationId, x.MarketExternalId })
                    .IsUnique()
                    .HasDatabaseName("IX_PurchaseCandidates_Org_MarketItem");
                entity.HasIndex(x => new { x.OrganizationId, x.Status, x.DemandScore });

                entity.Property(x => x.CategoryId).HasMaxLength(100);
                entity.Property(x => x.CategoryName).HasMaxLength(300);
                entity.Property(x => x.MarketExternalId).HasMaxLength(200);
                entity.Property(x => x.MarketName).HasMaxLength(500);
                entity.Property(x => x.MarketBrand).HasMaxLength(200);
                entity.Property(x => x.MarketModel).HasMaxLength(200);
                entity.Property(x => x.MarketImageUrl).HasMaxLength(1000);
                entity.Property(x => x.MarketPermalink).HasMaxLength(1000);
                entity.Property(x => x.MarketPriceMxn).HasColumnType("numeric(18,2)");
                entity.Property(x => x.Trend).HasMaxLength(20);
                entity.Property(x => x.SourceMarketplace).HasMaxLength(50);
                entity.Property(x => x.SourceProductId).HasMaxLength(200);
                entity.Property(x => x.SourceUrl).HasMaxLength(1000);
                entity.Property(x => x.SourceTitle).HasMaxLength(500);
                entity.Property(x => x.SourceImageUrl).HasMaxLength(1000);
                entity.Property(x => x.SourceCondition).HasMaxLength(100);
                entity.Property(x => x.SourcePriceUsd).HasColumnType("numeric(18,2)");
                entity.Property(x => x.SourceShippingUsd).HasColumnType("numeric(18,2)");
                entity.Property(x => x.SourceSearchQuery).HasMaxLength(300);
                entity.Property(x => x.LandedCostMxn).HasColumnType("numeric(18,2)");
                entity.Property(x => x.EstimatedNetProfitMxn).HasColumnType("numeric(18,2)");
                entity.Property(x => x.EstimatedMarginPercent).HasColumnType("numeric(8,2)");
                entity.Property(x => x.RecommendedListingPriceMxn).HasColumnType("numeric(18,2)");
                entity.Property(x => x.Status).HasMaxLength(30);
                entity.Property(x => x.PurchasedUnitCostUsd).HasColumnType("numeric(18,2)");
                entity.Property(x => x.Notes).HasMaxLength(500);
            });

            builder.Entity<MarketplaceSync.Web.Models.Inventory.InventoryLot>(entity =>
            {
                entity.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.ProductVariant).WithMany().HasForeignKey(x => x.ProductVariantId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => new { x.OrganizationId, x.ExpiresOn });
                entity.HasIndex(x => new { x.ProductVariantId, x.ExpiresOn });
            });

            builder.Entity<BuyerQuestion>(entity =>
            {
                entity.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.MarketplaceAccount).WithMany().HasForeignKey(x => x.MarketplaceAccountId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.MarketplaceAccountId, x.ExternalQuestionId }).IsUnique();
                entity.HasIndex(x => new { x.OrganizationId, x.Status, x.DateCreated });
                entity.Property(x => x.ExternalQuestionId).HasMaxLength(50);
                entity.Property(x => x.ItemId).HasMaxLength(50);
                entity.Property(x => x.Text).HasMaxLength(2000);
                entity.Property(x => x.Status).HasMaxLength(30);
                entity.Property(x => x.SuggestedAnswer).HasMaxLength(2000);
                entity.Property(x => x.AnswerText).HasMaxLength(2000);
                entity.Property(x => x.LastError).HasMaxLength(500);
            });

            builder.Entity<SalesOrder>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany(x => x.SalesOrders)
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.MarketplaceAccount)
                    .WithMany(x => x.SalesOrders)
                    .HasForeignKey(x => x.MarketplaceAccountId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(x => new { x.MarketplaceAccountId, x.ExternalOrderId })
                    .IsUnique();
                entity.HasIndex(x => new { x.OrganizationId, x.DateCreated });
                entity.HasIndex(x => new { x.OrganizationId, x.Status });
                entity.Property(x => x.Marketplace).HasMaxLength(50);
                entity.Property(x => x.ExternalOrderId).HasMaxLength(100);
                entity.Property(x => x.ExternalPackId).HasMaxLength(100);
                entity.Property(x => x.Status).HasMaxLength(50);
                entity.Property(x => x.StatusDetail).HasMaxLength(100);
                entity.Property(x => x.CurrencyId).HasMaxLength(20);
                entity.Property(x => x.BuyerId).HasMaxLength(100);
                entity.Property(x => x.BuyerNickname).HasMaxLength(200);
                entity.Property(x => x.ShippingId).HasMaxLength(100);
            });

            builder.Entity<SalesOrderItem>(entity =>
            {
                entity.HasOne(x => x.SalesOrder)
                    .WithMany(x => x.Items)
                    .HasForeignKey(x => x.SalesOrderId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(x => x.SalesOrderId);
                entity.Property(x => x.ExternalItemId).HasMaxLength(100);
                entity.Property(x => x.ExternalVariationId).HasMaxLength(100);
                entity.Property(x => x.Title).HasMaxLength(500);
                entity.Property(x => x.SellerSku).HasMaxLength(100);
                entity.Property(x => x.CurrencyId).HasMaxLength(20);
            });

            builder.Entity<InventoryMovement>(entity =>
            {
                entity.HasOne(x => x.Organization)
                    .WithMany()
                    .HasForeignKey(x => x.OrganizationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.ProductVariant)
                    .WithMany(x => x.InventoryMovements)
                    .HasForeignKey(x => x.ProductVariantId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(x => x.SalesOrder)
                    .WithMany()
                    .HasForeignKey(x => x.SalesOrderId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
                entity.HasIndex(x => new { x.ProductVariantId, x.CreatedAt });
                entity.HasIndex(x => new { x.OrganizationId, x.ReferenceKey })
                    .IsUnique()
                    .HasFilter("\"ReferenceKey\" IS NOT NULL");
                entity.Property(x => x.Type).HasMaxLength(40);
                entity.Property(x => x.ReferenceKey).HasMaxLength(300);
                entity.Property(x => x.Notes).HasMaxLength(500);
                entity.Property(x => x.CreatedByUserId).HasMaxLength(450);
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
