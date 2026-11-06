using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCompetitorsShippingBlacklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LabelPrintedAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PackedAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnCondition",
                table: "OrderClaims",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnRestockedAt",
                table: "OrderClaims",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlockedBuyers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceAccountId = table.Column<int>(type: "integer", nullable: false),
                    BuyerId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BlocksOrders = table.Column<bool>(type: "boolean", nullable: false),
                    BlocksQuestions = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedBuyers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CompetitorStores",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Permalink = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ReputationLevel = table.Column<int>(type: "integer", nullable: true),
                    PowerSeller = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TotalSales = table.Column<int>(type: "integer", nullable: true),
                    TopAppearances = table.Column<int>(type: "integer", nullable: false),
                    BestPosition = table.Column<int>(type: "integer", nullable: true),
                    Categories = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Followed = table.Column<bool>(type: "boolean", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitorStores", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedBuyers_OrganizationId_MarketplaceAccountId_BuyerId",
                table: "BlockedBuyers",
                columns: new[] { "OrganizationId", "MarketplaceAccountId", "BuyerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitorStores_OrganizationId_SellerId",
                table: "CompetitorStores",
                columns: new[] { "OrganizationId", "SellerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedBuyers");

            migrationBuilder.DropTable(
                name: "CompetitorStores");

            migrationBuilder.DropColumn(
                name: "LabelPrintedAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "PackedAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ReturnCondition",
                table: "OrderClaims");

            migrationBuilder.DropColumn(
                name: "ReturnRestockedAt",
                table: "OrderClaims");
        }
    }
}
