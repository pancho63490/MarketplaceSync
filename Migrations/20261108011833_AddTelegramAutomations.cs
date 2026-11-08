using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LabelsDaily",
                table: "AutomationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "LabelsHour",
                table: "AutomationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LabelsSentOn",
                table: "AutomationSettings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LabelsThermal",
                table: "AutomationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "RepricingMaxIncreasePercent",
                table: "AutomationSettings",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 25m);

            migrationBuilder.AddColumn<string>(
                name: "RepricingMode",
                table: "AutomationSettings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "alert");

            migrationBuilder.AddColumn<int>(
                name: "WeeklyPurchaseDay",
                table: "AutomationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyPurchaseEnabled",
                table: "AutomationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyPurchaseIncludeNew",
                table: "AutomationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WeeklyPurchaseSentOn",
                table: "AutomationSettings",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantTurns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Content = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTurns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BotActions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PublicationId = table.Column<int>(type: "integer", nullable: true),
                    Value = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Result = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseLists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    TotalUsd = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseListItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PurchaseListId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    CandidateId = table.Column<long>(type: "bigint", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitCostUsd = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseListItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseListItems_PurchaseLists_PurchaseListId",
                        column: x => x.PurchaseListId,
                        principalTable: "PurchaseLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTurns_OrganizationId_CreatedAt",
                table: "AssistantTurns",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BotActions_OrganizationId_PublicationId_CreatedAt",
                table: "BotActions",
                columns: new[] { "OrganizationId", "PublicationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BotActions_OrganizationId_Status",
                table: "BotActions",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseListItems_PurchaseListId",
                table: "PurchaseListItems",
                column: "PurchaseListId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseLists_OrganizationId_CreatedAt",
                table: "PurchaseLists",
                columns: new[] { "OrganizationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantTurns");

            migrationBuilder.DropTable(
                name: "BotActions");

            migrationBuilder.DropTable(
                name: "PurchaseListItems");

            migrationBuilder.DropTable(
                name: "PurchaseLists");

            migrationBuilder.DropColumn(
                name: "LabelsDaily",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "LabelsHour",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "LabelsSentOn",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "LabelsThermal",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "RepricingMaxIncreasePercent",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "RepricingMode",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "WeeklyPurchaseDay",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "WeeklyPurchaseEnabled",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "WeeklyPurchaseIncludeNew",
                table: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "WeeklyPurchaseSentOn",
                table: "AutomationSettings");
        }
    }
}
