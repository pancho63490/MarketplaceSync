using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260929120000_AddMarketplaceAccounts")]
public partial class AddMarketplaceAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MarketplaceAccounts",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                Marketplace = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                ExternalAccountId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DisconnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ConnectedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MarketplaceAccounts", x => x.Id);
                table.ForeignKey("FK_MarketplaceAccounts_Organizations_OrganizationId", x => x.OrganizationId,
                    "Organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_MarketplaceAccounts_AspNetUsers_ConnectedByUserId", x => x.ConnectedByUserId,
                    "AspNetUsers", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex("IX_MarketplaceAccounts_Org_Marketplace_ExternalId",
            "MarketplaceAccounts", new[] { "OrganizationId", "Marketplace", "ExternalAccountId" }, unique: true);
        migrationBuilder.CreateIndex("IX_MarketplaceAccounts_ConnectedByUserId", "MarketplaceAccounts", "ConnectedByUserId");

        migrationBuilder.AddColumn<int>("MarketplaceAccountId", "MercadoLibreTokens", type: "integer", nullable: true);

        migrationBuilder.Sql("""
            INSERT INTO "MarketplaceAccounts"
                ("OrganizationId", "Marketplace", "ExternalAccountId", "DisplayName", "Status", "ConnectedAt", "DisconnectedAt", "ConnectedByUserId")
            SELECT "OrganizationId", 'MercadoLibre', "UserId", MAX("Nickname"),
                   CASE WHEN bool_or("IsActive") THEN 'Connected' ELSE 'Disconnected' END,
                   MIN("CreatedAt"),
                   CASE WHEN bool_or("IsActive") THEN NULL ELSE MAX(COALESCE("UpdatedAt", "CreatedAt")) END,
                   MAX("ConnectedByUserId")
            FROM "MercadoLibreTokens"
            WHERE "UserId" IS NOT NULL AND "UserId" <> ''
            GROUP BY "OrganizationId", "UserId";

            UPDATE "MercadoLibreTokens" AS token
            SET "MarketplaceAccountId" = account."Id"
            FROM "MarketplaceAccounts" AS account
            WHERE account."OrganizationId" = token."OrganizationId"
              AND account."Marketplace" = 'MercadoLibre'
              AND account."ExternalAccountId" = token."UserId";
            """);

        migrationBuilder.CreateIndex("IX_MercadoLibreTokens_MarketplaceAccountId", "MercadoLibreTokens", "MarketplaceAccountId");
        migrationBuilder.AddForeignKey("FK_MercadoLibreTokens_MarketplaceAccounts_MarketplaceAccountId",
            "MercadoLibreTokens", "MarketplaceAccountId", "MarketplaceAccounts", "Id", onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_MercadoLibreTokens_MarketplaceAccounts_MarketplaceAccountId", "MercadoLibreTokens");
        migrationBuilder.DropIndex("IX_MercadoLibreTokens_MarketplaceAccountId", "MercadoLibreTokens");
        migrationBuilder.DropColumn("MarketplaceAccountId", "MercadoLibreTokens");
        migrationBuilder.DropTable("MarketplaceAccounts");
    }
}
