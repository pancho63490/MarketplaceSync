using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ThankYouSentAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThankYouStatus",
                table: "SalesOrders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoAnswered",
                table: "BuyerQuestions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AutoNote",
                table: "BuyerQuestions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Topic",
                table: "BuyerQuestions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutoAnswerEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AutoAnswerTopics = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AutoAnswerSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OffersInvoice = table.Column<bool>(type: "boolean", nullable: true),
                    ThankYouEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ThankYouMessage = table.Column<string>(type: "character varying(350)", maxLength: 350, nullable: false),
                    ThankYouSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationSettings_OrganizationId",
                table: "AutomationSettings",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationSettings");

            migrationBuilder.DropColumn(
                name: "ThankYouSentAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "ThankYouStatus",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "AutoAnswered",
                table: "BuyerQuestions");

            migrationBuilder.DropColumn(
                name: "AutoNote",
                table: "BuyerQuestions");

            migrationBuilder.DropColumn(
                name: "Topic",
                table: "BuyerQuestions");
        }
    }
}
