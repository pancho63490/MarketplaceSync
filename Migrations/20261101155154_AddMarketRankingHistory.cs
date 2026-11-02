using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketRankingHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketRankingSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceAccountId = table.Column<int>(type: "integer", nullable: true),
                    CategoryId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CategoryName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TotalActiveListings = table.Column<int>(type: "integer", nullable: true),
                    SnapshotDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketRankingSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketRankingSnapshots_MarketplaceAccounts_MarketplaceAccou~",
                        column: x => x.MarketplaceAccountId,
                        principalTable: "MarketplaceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MarketRankingSnapshots_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketRankingSnapshotItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MarketRankingSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Brand = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    CurrencyId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Permalink = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OpportunityScore = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketRankingSnapshotItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketRankingSnapshotItems_MarketRankingSnapshots_MarketRan~",
                        column: x => x.MarketRankingSnapshotId,
                        principalTable: "MarketRankingSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketRankingSnapshotItems_ExternalId",
                table: "MarketRankingSnapshotItems",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketRankingSnapshotItems_MarketRankingSnapshotId_External~",
                table: "MarketRankingSnapshotItems",
                columns: new[] { "MarketRankingSnapshotId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketRankingSnapshots_Daily",
                table: "MarketRankingSnapshots",
                columns: new[] { "OrganizationId", "CategoryId", "SnapshotDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketRankingSnapshots_MarketplaceAccountId",
                table: "MarketRankingSnapshots",
                column: "MarketplaceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketRankingSnapshots_OrganizationId_CategoryId_CapturedAt",
                table: "MarketRankingSnapshots",
                columns: new[] { "OrganizationId", "CategoryId", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketRankingSnapshotItems");

            migrationBuilder.DropTable(
                name: "MarketRankingSnapshots");
        }
    }
}
