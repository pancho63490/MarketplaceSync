using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoExchangeRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoExchangeRate",
                table: "CrossBorderSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRateMarkupPercent",
                table: "CrossBorderSettings",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ExchangeRateSource",
                table: "CrossBorderSettings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExchangeRateUpdatedAt",
                table: "CrossBorderSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OfficialUsdToMxnRate",
                table: "CrossBorderSettings",
                type: "numeric(10,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoExchangeRate",
                table: "CrossBorderSettings");

            migrationBuilder.DropColumn(
                name: "ExchangeRateMarkupPercent",
                table: "CrossBorderSettings");

            migrationBuilder.DropColumn(
                name: "ExchangeRateSource",
                table: "CrossBorderSettings");

            migrationBuilder.DropColumn(
                name: "ExchangeRateUpdatedAt",
                table: "CrossBorderSettings");

            migrationBuilder.DropColumn(
                name: "OfficialUsdToMxnRate",
                table: "CrossBorderSettings");
        }
    }
}
