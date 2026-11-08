using MarketplaceSync.Web.Models;

namespace MarketplaceSync.Services.Inventory;

/// <summary>
/// Stock de reserva: piezas por SKU que no se publican en Mercado Libre para no sobrevender
/// (temporada alta, conteos dudosos, ventas por fuera). Se publica stock − reserva, nunca menos de 0.
/// </summary>
public static class StockReserve
{
    public static int ReserveFor(ProductVariant variant, int general) => Math.Max(0, variant.StockReserve ?? general);

    public static int Publishable(ProductVariant variant, int general) =>
        Math.Max(0, Math.Max(0, variant.Stock) - ReserveFor(variant, general));

    public static int Publishable(IEnumerable<ProductVariant> variants, int general) =>
        variants.Sum(v => Publishable(v, general));
}
