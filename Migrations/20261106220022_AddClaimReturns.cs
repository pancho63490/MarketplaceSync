using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddClaimReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasReturn",
                table: "OrderClaims",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReturnUnits",
                table: "OrderClaims",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasReturn",
                table: "OrderClaims");

            migrationBuilder.DropColumn(
                name: "ReturnUnits",
                table: "OrderClaims");
        }
    }
}
