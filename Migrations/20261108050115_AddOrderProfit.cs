using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderProfit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ProductCostMxn",
                table: "SalesOrders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProfitComputedAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RealProfitMxn",
                table: "SalesOrders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SellerShippingCostMxn",
                table: "SalesOrders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShippingCostIsReal",
                table: "SalesOrders",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxWithheldMxn",
                table: "SalesOrders",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProductCostMxn",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ProfitComputedAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "RealProfitMxn",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "SellerShippingCostMxn",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ShippingCostIsReal",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "TaxWithheldMxn",
                table: "SalesOrders");
        }
    }
}
