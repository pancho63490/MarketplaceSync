using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPostSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderClaims",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceAccountId = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SalesOrderId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Stage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReasonId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PendingActions = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DateCreated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderClaims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceAccountId = table.Column<int>(type: "integer", nullable: false),
                    PackId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SalesOrderId = table.Column<int>(type: "integer", nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FromSeller = table.Column<bool>(type: "boolean", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderClaims_OrganizationId_ExternalId",
                table: "OrderClaims",
                columns: new[] { "OrganizationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderClaims_OrganizationId_Status_DueDate",
                table: "OrderClaims",
                columns: new[] { "OrganizationId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderMessages_OrganizationId_ExternalId",
                table: "OrderMessages",
                columns: new[] { "OrganizationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderMessages_OrganizationId_PackId_SentAt",
                table: "OrderMessages",
                columns: new[] { "OrganizationId", "PackId", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderClaims");

            migrationBuilder.DropTable(
                name: "OrderMessages");
        }
    }
}
