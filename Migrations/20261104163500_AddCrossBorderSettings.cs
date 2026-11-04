using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCrossBorderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrossBorderSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsdToMxnRate = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    ImportFeePercent = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    AdditionalHandlingFeeUsd = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    TargetProfitMarginPercent = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    MinProfitAmountMxn = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    EstimatedShippingMxn = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    DefaultMarketplaceFeePercent = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    PlatformTaxWithholdingPercent = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    AutoPublishEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossBorderSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrossBorderSettings_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrossBorderSettings_OrganizationId",
                table: "CrossBorderSettings",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrossBorderSettings");
        }
    }
}
