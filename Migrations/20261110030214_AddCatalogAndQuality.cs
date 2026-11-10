using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogAndQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CatalogCheckedAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CatalogListing",
                table: "MarketplacePublications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CatalogOpportunities",
                table: "MarketplacePublications",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CatalogPriceToWin",
                table: "MarketplacePublications",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogProductId",
                table: "MarketplacePublications",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogReasons",
                table: "MarketplacePublications",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogStatus",
                table: "MarketplacePublications",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CatalogWinnerPrice",
                table: "MarketplacePublications",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityActionsJson",
                table: "MarketplacePublications",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QualityCheckedAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityLevel",
                table: "MarketplacePublications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QualityScore",
                table: "MarketplacePublications",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CatalogCheckedAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "CatalogListing",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "CatalogOpportunities",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "CatalogPriceToWin",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "CatalogProductId",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "CatalogReasons",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "CatalogStatus",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "CatalogWinnerPrice",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "QualityActionsJson",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "QualityCheckedAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "QualityLevel",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "QualityScore",
                table: "MarketplacePublications");
        }
    }
}
