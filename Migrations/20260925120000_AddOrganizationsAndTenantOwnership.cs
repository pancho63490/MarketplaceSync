using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260925120000_AddOrganizationsAndTenantOwnership")]
public partial class AddOrganizationsAndTenantOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Organizations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                Slug = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Organizations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "OrganizationMemberships",
            columns: table => new
            {
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                Role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrganizationMemberships", x => new { x.OrganizationId, x.UserId });
                table.ForeignKey("FK_OrganizationMemberships_Organizations_OrganizationId", x => x.OrganizationId,
                    "Organizations", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_OrganizationMemberships_AspNetUsers_UserId", x => x.UserId,
                    "AspNetUsers", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Organizations_Slug", "Organizations", "Slug", unique: true);
        migrationBuilder.CreateIndex("IX_OrganizationMemberships_UserId", "OrganizationMemberships", "UserId");

        migrationBuilder.Sql("""
            INSERT INTO "Organizations" ("Id", "Name", "Slug", "IsActive", "CreatedAt")
            SELECT md5('user:' || u."Id")::uuid,
                   COALESCE(NULLIF(u."Email", ''), 'Mi negocio'),
                   'migrated-' || md5(u."Id"), true, now()
            FROM "AspNetUsers" u;

            INSERT INTO "Organizations" ("Id", "Name", "Slug", "IsActive", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-000000000001'::uuid, 'Negocio heredado', 'legacy-workspace', true, now());

            INSERT INTO "OrganizationMemberships" ("OrganizationId", "UserId", "Role", "IsActive", "CreatedAt")
            SELECT md5('user:' || u."Id")::uuid, u."Id", 'Owner', true, now()
            FROM "AspNetUsers" u;
            """);

        migrationBuilder.AddColumn<Guid>("OrganizationId", "Products", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("CreatedByUserId", "Products", type: "character varying(450)", maxLength: 450, nullable: true);
        migrationBuilder.AddColumn<Guid>("OrganizationId", "MercadoLibreTokens", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("ConnectedByUserId", "MercadoLibreTokens", type: "character varying(450)", maxLength: 450, nullable: true);

        migrationBuilder.Sql("""
            UPDATE "Products" p
            SET "OrganizationId" = COALESCE(
                    (SELECT md5('user:' || u."Id")::uuid FROM "AspNetUsers" u
                     WHERE u."Id" = COALESCE(NULLIF(p."AppUserId", ''), NULLIF(p."UserId", ''))),
                    '00000000-0000-0000-0000-000000000001'::uuid),
                "CreatedByUserId" = COALESCE(
                    (SELECT u."Id" FROM "AspNetUsers" u
                     WHERE u."Id" = COALESCE(NULLIF(p."AppUserId", ''), NULLIF(p."UserId", ''))),
                    NULL);

            UPDATE "MercadoLibreTokens" t
            SET "OrganizationId" = COALESCE(
                    (SELECT md5('user:' || u."Id")::uuid FROM "AspNetUsers" u
                     WHERE u."Id" = NULLIF(t."AppUserId", '')),
                    '00000000-0000-0000-0000-000000000001'::uuid),
                "ConnectedByUserId" = (SELECT u."Id" FROM "AspNetUsers" u
                     WHERE u."Id" = NULLIF(t."AppUserId", ''));
            """);

        migrationBuilder.AlterColumn<Guid>("OrganizationId", "Products", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AlterColumn<Guid>("OrganizationId", "MercadoLibreTokens", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

        migrationBuilder.DropForeignKey("FK_Products_AspNetUsers_UserId", "Products");
        migrationBuilder.DropIndex("IX_Products_UserId", "Products");
        migrationBuilder.DropColumn("UserId", "Products");
        migrationBuilder.DropColumn("AppUserId", "Products");
        migrationBuilder.DropColumn("AppUserId", "MercadoLibreTokens");

        migrationBuilder.CreateIndex("IX_Products_OrganizationId_CreatedAt", "Products",
            new[] { "OrganizationId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_Products_OrganizationId_SourceMarketplace_SourceProductId", "Products",
            new[] { "OrganizationId", "SourceMarketplace", "SourceProductId" });
        migrationBuilder.CreateIndex("IX_MercadoLibreTokens_OrganizationId_UserId", "MercadoLibreTokens",
            new[] { "OrganizationId", "UserId" }, unique: true);

        migrationBuilder.AddForeignKey("FK_Products_Organizations_OrganizationId", "Products", "OrganizationId",
            "Organizations", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey("FK_Products_AspNetUsers_CreatedByUserId", "Products", "CreatedByUserId",
            "AspNetUsers", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
        migrationBuilder.AddForeignKey("FK_MercadoLibreTokens_Organizations_OrganizationId", "MercadoLibreTokens",
            "OrganizationId", "Organizations", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
        migrationBuilder.AddForeignKey("FK_MercadoLibreTokens_AspNetUsers_ConnectedByUserId", "MercadoLibreTokens",
            "ConnectedByUserId", "AspNetUsers", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "La migración conserva y transforma datos de propietarios existentes; no se puede revertir automáticamente sin riesgo de pérdida.");
    }
}
