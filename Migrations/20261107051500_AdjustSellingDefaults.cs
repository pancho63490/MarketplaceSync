using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceSync.Web.Migrations
{
    /// <summary>
    /// Ajusta los costos de venta a lo que cobra Mercado Libre hoy (resumen de cargos de nov 2026):
    /// Premium 15.5%, retención sin RFC 31.03% del precio (20% ISR + 16% IVA sobre el precio sin IVA),
    /// envío $122.50. Las publicaciones que aún no se suben pasan a Premium.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261107051500_AdjustSellingDefaults")]
    public partial class AdjustSellingDefaults : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "CrossBorderSettings"
                SET "DefaultMarketplaceFeePercent" = 15.50,
                    "PlatformTaxWithholdingPercent" = 31.03,
                    "EstimatedShippingMxn" = 122.50;
                """);

            migrationBuilder.Sql("""
                UPDATE "MarketplacePublications"
                SET "ListingTypeId" = 'gold_pro'
                WHERE ("ExternalItemId" IS NULL OR "ExternalItemId" = '')
                  AND ("ListingTypeId" IS NULL OR "ListingTypeId" = 'gold_special');
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Solo ajusta datos; no hay nada que deshacer en el esquema.
        }
    }
}
