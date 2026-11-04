using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicationSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastStockPushAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStockPushError",
                table: "MarketplacePublications",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncedAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaleFeeForPrice",
                table: "MarketplacePublications",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaleFeeMxn",
                table: "MarketplacePublications",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SoldQuantity",
                table: "MarketplacePublications",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketplacePublications_ExternalItemId",
                table: "MarketplacePublications",
                column: "ExternalItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketplacePublications_ExternalItemId",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "LastStockPushAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "LastStockPushError",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "LastSyncedAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "SaleFeeForPrice",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "SaleFeeMxn",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "SoldQuantity",
                table: "MarketplacePublications");
        }
    }
}
