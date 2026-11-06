using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddImportAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ImportAgentEnabled",
                table: "CrossBorderSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AgentRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Engine = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Reviewed = table.Column<int>(type: "integer", nullable: false),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    Discarded = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgentRunId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CandidateId = table.Column<long>(type: "bigint", nullable: true),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSteps_AgentRuns_AgentRunId",
                        column: x => x.AgentRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_OrganizationId_StartedAt",
                table: "AgentRuns",
                columns: new[] { "OrganizationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSteps_AgentRunId",
                table: "AgentSteps",
                column: "AgentRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSteps");

            migrationBuilder.DropTable(
                name: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "ImportAgentEnabled",
                table: "CrossBorderSettings");
        }
    }
}
