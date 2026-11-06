using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFixPhotosOnPublish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FixPhotosOnPublish",
                table: "AutomationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FixPhotosOnPublish",
                table: "AutomationSettings");
        }
    }
}
