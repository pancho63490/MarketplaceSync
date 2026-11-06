using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddShippingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ShippingCheckedAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShippingDeadline",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingLogistic",
                table: "SalesOrders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingStatus",
                table: "SalesOrders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShippingSubstatus",
                table: "SalesOrders",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShippingCheckedAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ShippingDeadline",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ShippingLogistic",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ShippingStatus",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ShippingSubstatus",
                table: "SalesOrders");
        }
    }
}
