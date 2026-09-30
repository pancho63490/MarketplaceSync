using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260929130000_SplitMarketplacePublicationFromProduct")]
public partial class SplitMarketplacePublicationFromProduct : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MarketplaceAccountId",
            table: "MarketplacePublications",
            type: "integer",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "MarketplacePublications" AS publication
            SET "MarketplaceAccountId" = CASE
                    WHEN (SELECT COUNT(*) FROM "MarketplaceAccounts" account
                          WHERE account."OrganizationId" = product."OrganizationId"
                            AND account."Marketplace" = 'MercadoLibre') = 1
                    THEN (SELECT MIN(account."Id") FROM "MarketplaceAccounts" account
                          WHERE account."OrganizationId" = product."OrganizationId"
                            AND account."Marketplace" = 'MercadoLibre')
                    ELSE publication."MarketplaceAccountId"
                END,
                "ExternalItemId" = COALESCE(product."MercadoLibreItemId", publication."ExternalItemId"),
                "Permalink" = COALESCE(product."MercadoLibrePermalink", publication."Permalink"),
                "Price" = COALESCE(product."MercadoLibrePrice", publication."Price"),
                "Stock" = COALESCE(product."MercadoLibreStock", publication."Stock"),
                "CurrencyId" = COALESCE(product."MercadoLibreCurrencyId", publication."CurrencyId"),
                "CategoryId" = COALESCE(product."MercadoLibreCategoryId", publication."CategoryId"),
                "ListingTypeId" = COALESCE(product."MercadoLibreListingTypeId", publication."ListingTypeId"),
                "Condition" = COALESCE(product."MercadoLibreCondition", publication."Condition"),
                "Status" = COALESCE(product."MercadoLibreStatus", publication."Status"),
                "IsPublished" = publication."IsPublished" OR product."MercadoLibreItemId" IS NOT NULL,
                "PublishedAt" = COALESCE(product."MercadoLibrePublishedAt", publication."PublishedAt"),
                "UpdatedAt" = COALESCE(product."UpdatedAt", publication."UpdatedAt")
            FROM "Products" AS product
            WHERE publication."ProductId" = product."Id"
              AND publication."Marketplace" = 'MercadoLibre'
              AND publication."Id" = (
                  SELECT MIN(existing."Id") FROM "MarketplacePublications" existing
                  WHERE existing."ProductId" = product."Id"
                    AND existing."Marketplace" = 'MercadoLibre'
              );

            INSERT INTO "MarketplacePublications"
                ("ProductId", "MarketplaceAccountId", "Marketplace", "ExternalItemId", "Permalink",
                 "Price", "Stock", "CurrencyId", "CategoryId", "ListingTypeId", "Condition", "Status",
                 "IsPublished", "PublishedAt", "CreatedAt", "UpdatedAt")
            SELECT product."Id",
                   CASE WHEN (SELECT COUNT(*) FROM "MarketplaceAccounts" account
                              WHERE account."OrganizationId" = product."OrganizationId"
                                AND account."Marketplace" = 'MercadoLibre') = 1
                        THEN (SELECT MIN(account."Id") FROM "MarketplaceAccounts" account
                              WHERE account."OrganizationId" = product."OrganizationId"
                                AND account."Marketplace" = 'MercadoLibre')
                        ELSE NULL END,
                   'MercadoLibre', product."MercadoLibreItemId", product."MercadoLibrePermalink",
                   product."MercadoLibrePrice", product."MercadoLibreStock", product."MercadoLibreCurrencyId",
                   product."MercadoLibreCategoryId", product."MercadoLibreListingTypeId", product."MercadoLibreCondition",
                   product."MercadoLibreStatus", product."MercadoLibreItemId" IS NOT NULL,
                   product."MercadoLibrePublishedAt", product."CreatedAt", COALESCE(product."UpdatedAt", product."CreatedAt")
            FROM "Products" product
            WHERE (product."MercadoLibreItemId" IS NOT NULL OR product."MercadoLibreCategoryId" IS NOT NULL
                   OR product."MercadoLibrePrice" IS NOT NULL OR product."MercadoLibreStock" IS NOT NULL
                   OR product."MercadoLibreStatus" IS NOT NULL OR product."MercadoLibrePermalink" IS NOT NULL)
              AND NOT EXISTS (
                  SELECT 1 FROM "MarketplacePublications" publication
                  WHERE publication."ProductId" = product."Id"
                    AND publication."Marketplace" = 'MercadoLibre'
              );
            """);

        migrationBuilder.CreateIndex(
            name: "IX_MarketplacePublications_MarketplaceAccountId",
            table: "MarketplacePublications",
            column: "MarketplaceAccountId");
        migrationBuilder.CreateIndex(
            name: "IX_MarketplacePublications_Product_Channel_Account",
            table: "MarketplacePublications",
            columns: new[] { "ProductId", "Marketplace", "MarketplaceAccountId" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_MarketplacePublications_MarketplaceAccounts_MarketplaceAccountId",
            table: "MarketplacePublications",
            column: "MarketplaceAccountId",
            principalTable: "MarketplaceAccounts",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.DropColumn("MercadoLibreItemId", "Products");
        migrationBuilder.DropColumn("MercadoLibreCategoryId", "Products");
        migrationBuilder.DropColumn("MercadoLibrePrice", "Products");
        migrationBuilder.DropColumn("MercadoLibreStock", "Products");
        migrationBuilder.DropColumn("MercadoLibreCurrencyId", "Products");
        migrationBuilder.DropColumn("MercadoLibreListingTypeId", "Products");
        migrationBuilder.DropColumn("MercadoLibreCondition", "Products");
        migrationBuilder.DropColumn("MercadoLibreStatus", "Products");
        migrationBuilder.DropColumn("MercadoLibrePermalink", "Products");
        migrationBuilder.DropColumn("MercadoLibrePublishedAt", "Products");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "This migration supports multiple channel publications per product; restore from backup to reverse it without data loss.");
    }
}
