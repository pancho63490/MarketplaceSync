using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CategoryName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MarketExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MarketName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MarketBrand = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MarketModel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MarketImageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MarketPermalink = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MarketPriceMxn = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    BestPosition = table.Column<int>(type: "integer", nullable: false),
                    LatestPosition = table.Column<int>(type: "integer", nullable: true),
                    DaysInTop = table.Column<int>(type: "integer", nullable: false),
                    SnapshotDaysObserved = table.Column<int>(type: "integer", nullable: false),
                    Trend = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DemandScore = table.Column<int>(type: "integer", nullable: false),
                    SourceMarketplace = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SourceProductId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SourceImageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceCondition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourcePriceUsd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    SourceShippingUsd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    SourceSearchQuery = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MatchConfidence = table.Column<int>(type: "integer", nullable: false),
                    LastSourceSearchAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LandedCostMxn = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    EstimatedNetProfitMxn = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    EstimatedMarginPercent = table.Column<decimal>(type: "numeric(8,2)", nullable: true),
                    RecommendedListingPriceMxn = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    SuggestedQuantity = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PurchasedQuantity = table.Column<int>(type: "integer", nullable: true),
                    PurchasedUnitCostUsd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    PurchasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    MarketplaceAccountId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastEvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseCandidates_MarketplaceAccounts_MarketplaceAccountId",
                        column: x => x.MarketplaceAccountId,
                        principalTable: "MarketplaceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PurchaseCandidates_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PurchaseCandidates_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseCandidates_MarketplaceAccountId",
                table: "PurchaseCandidates",
                column: "MarketplaceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseCandidates_Org_MarketItem",
                table: "PurchaseCandidates",
                columns: new[] { "OrganizationId", "MarketExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseCandidates_OrganizationId_Status_DemandScore",
                table: "PurchaseCandidates",
                columns: new[] { "OrganizationId", "Status", "DemandScore" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseCandidates_ProductId",
                table: "PurchaseCandidates",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseCandidates");
        }
    }
}
