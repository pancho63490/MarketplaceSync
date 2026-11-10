using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoPriceCause",
                table: "MarketplacePublications",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoPriceCheckedAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AutoPriceMax",
                table: "MarketplacePublications",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AutoPriceMin",
                table: "MarketplacePublications",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoPriceRule",
                table: "MarketplacePublications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoPriceStatus",
                table: "MarketplacePublications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPriceCause",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "AutoPriceCheckedAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "AutoPriceMax",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "AutoPriceMin",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "AutoPriceRule",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "AutoPriceStatus",
                table: "MarketplacePublications");
        }
    }
}
