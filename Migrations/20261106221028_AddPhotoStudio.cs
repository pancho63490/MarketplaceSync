using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoStudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalImageUrl",
                table: "Products",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoCheck",
                table: "Products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhotoCheckedAt",
                table: "Products",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotoHeight",
                table: "Products",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotoWidth",
                table: "Products",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalImageUrl",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoCheck",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoCheckedAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoHeight",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PhotoWidth",
                table: "Products");
        }
    }
}
