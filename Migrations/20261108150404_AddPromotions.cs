using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PromotionCheckedAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromotionEndsAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionId",
                table: "MarketplacePublications",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PromotionMeliPercent",
                table: "MarketplacePublications",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionOfferId",
                table: "MarketplacePublications",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PromotionOriginalPrice",
                table: "MarketplacePublications",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PromotionPrice",
                table: "MarketplacePublications",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionStatus",
                table: "MarketplacePublications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionType",
                table: "MarketplacePublications",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "BotActions",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PromotionAlerts",
                table: "AutomationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PromotionsCheckedOn",
                table: "AutomationSettings",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromotionCheckedAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionEndsAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionId",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionMeliPercent",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionOfferId",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionOriginalPrice",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionPrice",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionStatus",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "PromotionType",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "Payload",
                table: "BotActions");

            migrationBuilder.DropColumn(
                name: "PromotionAlerts",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "PromotionsCheckedOn",
                table: "AutomationSettings");
        }
    }
}
