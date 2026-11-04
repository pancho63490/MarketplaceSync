using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicationAutopilotFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributesJson",
                table: "MarketplacePublications",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutopilotNotes",
                table: "MarketplacePublications",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttributesJson",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "AutopilotNotes",
                table: "MarketplacePublications");
        }
    }
}
