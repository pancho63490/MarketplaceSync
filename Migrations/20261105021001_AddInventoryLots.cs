using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryLots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryLots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: false),
                    LotCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                    QuantityReceived = table.Column<int>(type: "integer", nullable: false),
                    QuantityRemaining = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WrittenOffAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryLots_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryLots_ProductVariants_ProductVariantId",
                        column: x => x.ProductVariantId,
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_OrganizationId_ExpiresOn",
                table: "InventoryLots",
                columns: new[] { "OrganizationId", "ExpiresOn" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_ProductVariantId_ExpiresOn",
                table: "InventoryLots",
                columns: new[] { "ProductVariantId", "ExpiresOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryLots");
        }
    }
}
