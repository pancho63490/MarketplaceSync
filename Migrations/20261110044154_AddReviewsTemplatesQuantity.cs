using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewsTemplatesQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "QuantityPricesCheckedAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuantityPricesJson",
                table: "MarketplacePublications",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RatingAverage",
                table: "MarketplacePublications",
                type: "numeric(3,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RatingCount",
                table: "MarketplacePublications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewsCheckedAt",
                table: "MarketplacePublications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemReviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationId = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Rate = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BuyingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemReviews_MarketplacePublications_PublicationId",
                        column: x => x.PublicationId,
                        principalTable: "MarketplacePublications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Text = table.Column<string>(type: "character varying(350)", maxLength: 350, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UseCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemReviews_OrganizationId_CreatedAt",
                table: "ItemReviews",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemReviews_OrganizationId_ExternalId",
                table: "ItemReviews",
                columns: new[] { "OrganizationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemReviews_PublicationId",
                table: "ItemReviews",
                column: "PublicationId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_OrganizationId_SortOrder",
                table: "MessageTemplates",
                columns: new[] { "OrganizationId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemReviews");

            migrationBuilder.DropTable(
                name: "MessageTemplates");

            migrationBuilder.DropColumn(
                name: "QuantityPricesCheckedAt",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "QuantityPricesJson",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "RatingAverage",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "RatingCount",
                table: "MarketplacePublications");

            migrationBuilder.DropColumn(
                name: "ReviewsCheckedAt",
                table: "MarketplacePublications");
        }
    }
}
