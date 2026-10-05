using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004090000_AddProductVariants")]
public partial class AddProductVariants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProductVariants",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ProductId = table.Column<int>(type: "integer", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                AttributesJson = table.Column<string>(type: "jsonb", nullable: false),
                Barcode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Price = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                Stock = table.Column<int>(type: "integer", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductVariants", x => x.Id);
                table.ForeignKey("FK_ProductVariants_Organizations_OrganizationId", x => x.OrganizationId,
                    "Organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_ProductVariants_Products_ProductId", x => x.ProductId,
                    "Products", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProductVariants_OrganizationId_Sku",
            table: "ProductVariants",
            columns: new[] { "OrganizationId", "Sku" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProductVariants_ProductId_IsActive",
            table: "ProductVariants",
            columns: new[] { "ProductId", "IsActive" });

        migrationBuilder.CreateTable(
            name: "MarketplacePublicationVariants",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MarketplacePublicationId = table.Column<int>(type: "integer", nullable: false),
                ProductVariantId = table.Column<int>(type: "integer", nullable: false),
                ExternalVariationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MarketplacePublicationVariants", x => x.Id);
                table.ForeignKey("FK_MarketplacePublicationVariants_MarketplacePublications_MarketplacePublicationId", x => x.MarketplacePublicationId,
                    "MarketplacePublications", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_MarketplacePublicationVariants_ProductVariants_ProductVariantId", x => x.ProductVariantId,
                    "ProductVariants", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MarketplacePublicationVariants_MarketplacePublicationId_ProductVariantId",
            table: "MarketplacePublicationVariants",
            columns: new[] { "MarketplacePublicationId", "ProductVariantId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MarketplacePublicationVariants_ProductVariantId",
            table: "MarketplacePublicationVariants",
            column: "ProductVariantId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MarketplacePublicationVariants");
        migrationBuilder.DropTable(name: "ProductVariants");
    }
}
