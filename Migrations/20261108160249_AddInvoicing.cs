using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BillingCheckedAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerRfc",
                table: "SalesOrders",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalesInvoiceId",
                table: "SalesOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SatProductCode",
                table: "Products",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvoicingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuerRfc = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    IssuerName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FiscalRegime = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    ExpeditionZip = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    Serie = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NextFolio = table.Column<int>(type: "integer", nullable: false),
                    GlobalSerie = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NextGlobalFolio = table.Column<int>(type: "integer", nullable: false),
                    DefaultProductCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IvaRate = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    PaymentForm = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    UploadToMercadoLibre = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyTelegram = table.Column<bool>(type: "boolean", nullable: false),
                    CsdUploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CsdStatus = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoicingSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesInvoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Uuid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Serie = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Folio = table.Column<int>(type: "integer", nullable: false),
                    ReceiverRfc = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    ReceiverName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CfdiUse = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    MercadoLibrePackId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    MarketplaceAccountId = table.Column<int>(type: "integer", nullable: true),
                    UploadedToMercadoLibreAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UploadError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PeriodYear = table.Column<int>(type: "integer", nullable: true),
                    PeriodMonth = table.Column<int>(type: "integer", nullable: true),
                    OrdersCount = table.Column<int>(type: "integer", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelMotive = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesInvoices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoicingSettings_OrganizationId",
                table: "InvoicingSettings",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_OrganizationId_IssuedAt",
                table: "SalesInvoices",
                columns: new[] { "OrganizationId", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_OrganizationId_Kind_PeriodYear_PeriodMonth",
                table: "SalesInvoices",
                columns: new[] { "OrganizationId", "Kind", "PeriodYear", "PeriodMonth" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoicingSettings");

            migrationBuilder.DropTable(
                name: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "BillingCheckedAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "BuyerRfc",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "SalesInvoiceId",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "SatProductCode",
                table: "Products");
        }
    }
}
