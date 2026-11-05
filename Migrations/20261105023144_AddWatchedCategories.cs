using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchedCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PreparedAt",
                table: "PurchaseCandidates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoPrepareWeekly",
                table: "CrossBorderSettings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<bool>(
                name: "PublishOnReceipt",
                table: "CrossBorderSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "WatchedCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PathText = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false),
                    ParentWatchedCategoryId = table.Column<int>(type: "integer", nullable: true),
                    IsLeaf = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastExpandedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastItemsFound = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchedCategories_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchedCategories_WatchedCategories_ParentWatchedCategoryId",
                        column: x => x.ParentWatchedCategoryId,
                        principalTable: "WatchedCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchedCategories_OrganizationId_CategoryId",
                table: "WatchedCategories",
                columns: new[] { "OrganizationId", "CategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchedCategories_OrganizationId_IsLeaf_Enabled_LastScanned~",
                table: "WatchedCategories",
                columns: new[] { "OrganizationId", "IsLeaf", "Enabled", "LastScannedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchedCategories_ParentWatchedCategoryId",
                table: "WatchedCategories",
                column: "ParentWatchedCategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchedCategories");

            migrationBuilder.DropColumn(
                name: "PreparedAt",
                table: "PurchaseCandidates");

            migrationBuilder.DropColumn(
                name: "AutoPrepareWeekly",
                table: "CrossBorderSettings");

            migrationBuilder.DropColumn(
                name: "PublishOnReceipt",
                table: "CrossBorderSettings");
        }
    }
}
