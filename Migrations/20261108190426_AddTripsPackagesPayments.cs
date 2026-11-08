using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTripsPackagesPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MpCheckedAt",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpError",
                table: "SalesOrders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MpExpectedNet",
                table: "SalesOrders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MpNetReceived",
                table: "SalesOrders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpPaymentId",
                table: "SalesOrders",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MpReleaseDate",
                table: "SalesOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpReleaseStatus",
                table: "SalesOrders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RealLandedCostAt",
                table: "Products",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RealLandedCostMxn",
                table: "Products",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RealLandedTripId",
                table: "Products",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InboundPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Carrier = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SupplyTripId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    LastEvent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Location = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    LastEventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EstimatedDelivery = table.Column<DateOnly>(type: "date", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Registered = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NotifiedStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundPackages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplyTrips",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    FuelMxn = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    CrossingMxn = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    CustomsTaxesMxn = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    OtherMxn = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PurchaseListId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplyTrips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplyTripItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplyTripId = table.Column<int>(type: "integer", nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitCostUsd = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    ShippingUsd = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Store = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    RealUnitCostMxn = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    EstimatedUnitCostMxn = table.Column<decimal>(type: "numeric(12,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplyTripItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplyTripItems_SupplyTrips_SupplyTripId",
                        column: x => x.SupplyTripId,
                        principalTable: "SupplyTrips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboundPackages_OrganizationId_Status",
                table: "InboundPackages",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundPackages_OrganizationId_TrackingNumber",
                table: "InboundPackages",
                columns: new[] { "OrganizationId", "TrackingNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplyTripItems_SupplyTripId",
                table: "SupplyTripItems",
                column: "SupplyTripId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplyTrips_OrganizationId_Date",
                table: "SupplyTrips",
                columns: new[] { "OrganizationId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboundPackages");

            migrationBuilder.DropTable(
                name: "SupplyTripItems");

            migrationBuilder.DropTable(
                name: "SupplyTrips");

            migrationBuilder.DropColumn(
                name: "MpCheckedAt",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "MpError",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "MpExpectedNet",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "MpNetReceived",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "MpPaymentId",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "MpReleaseDate",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "MpReleaseStatus",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "RealLandedCostAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RealLandedCostMxn",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RealLandedTripId",
                table: "Products");
        }
    }
}
