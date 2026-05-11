using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class UpdateMercadoLibreTokenScopeLengt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MercadoLibreCategoryId",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoLibreCondition",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoLibreCurrencyId",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoLibreItemId",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoLibreListingTypeId",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoLibrePermalink",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MercadoLibrePrice",
                table: "Products",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MercadoLibrePublishedAt",
                table: "Products",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoLibreStatus",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MercadoLibreStock",
                table: "Products",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MercadoLibreCategoryId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibreCondition",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibreCurrencyId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibreItemId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibreListingTypeId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibrePermalink",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibrePrice",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibrePublishedAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibreStatus",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MercadoLibreStock",
                table: "Products");
        }
    }
}
