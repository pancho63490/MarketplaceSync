using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LowStockThreshold",
                table: "ProductVariants",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: false),
                    SalesOrderId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    QuantityDelta = table.Column<int>(type: "integer", nullable: false),
                    StockAfter = table.Column<int>(type: "integer", nullable: false),
                    ReferenceKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_ProductVariants_ProductVariantId",
                        column: x => x.ProductVariantId,
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_OrganizationId_CreatedAt",
                table: "InventoryMovements",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_OrganizationId_ReferenceKey",
                table: "InventoryMovements",
                columns: new[] { "OrganizationId", "ReferenceKey" },
                unique: true,
                filter: "\"ReferenceKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ProductVariantId_CreatedAt",
                table: "InventoryMovements",
                columns: new[] { "ProductVariantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SalesOrderId",
                table: "InventoryMovements",
                column: "SalesOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "LowStockThreshold",
                table: "ProductVariants");
        }
    }
}
