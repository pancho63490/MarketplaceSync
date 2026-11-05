using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    TelegramChatId = table.Column<long>(type: "bigint", nullable: true),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TelegramBotToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TelegramBotUsername = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TelegramChatId = table.Column<long>(type: "bigint", nullable: true),
                    TelegramChatName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LinkCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateOffset = table.Column<long>(type: "bigint", nullable: false),
                    PublicBaseUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NotifyQuestions = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    NotifyOrders = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    NotifyStock = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    DailySummary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    DailySummaryHour = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                    LastDailySummaryOn = table.Column<DateOnly>(type: "date", nullable: true),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastErrorAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationSettings_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_OrganizationId_Key",
                table: "NotificationLogs",
                columns: new[] { "OrganizationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_SentAt",
                table: "NotificationLogs",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_TelegramChatId_TelegramMessageId",
                table: "NotificationLogs",
                columns: new[] { "TelegramChatId", "TelegramMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSettings_LinkCode",
                table: "NotificationSettings",
                column: "LinkCode");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSettings_OrganizationId",
                table: "NotificationSettings",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationLogs");

            migrationBuilder.DropTable(
                name: "NotificationSettings");
        }
    }
}
