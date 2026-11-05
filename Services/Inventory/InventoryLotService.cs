using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Inventory;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Inventory;

public sealed record ExpiringLot(
    long LotId,
    int VariantId,
    int ProductId,
    string Sku,
    string? ProductTitle,
    string? LotCode,
    DateOnly ExpiresOn,
    int QuantityRemaining,
    int DaysLeft);

/// <summary>
/// Lotes con caducidad. Las salidas (ventas y ajustes a la baja) se descuentan del lote que caduca primero (FEFO).
/// Los lotes no cambian el stock por sí solos: acompañan cada movimiento de inventario.
/// </summary>
public sealed class InventoryLotService
{
    /// <summary>Días antes de caducar en que se avisa.</summary>
    public const int WarningDays = 30;

    private readonly AppDbContext _context;

    public InventoryLotService(AppDbContext context) => _context = context;

    /// <summary>Hoy en Ciudad Juárez (UTC-7), que es donde está el inventario.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));

    public static string? ValidateExpiry(DateOnly? expiresOn)
    {
        if (expiresOn is null) return null;
        if (expiresOn.Value < Today.AddYears(-1)) return "La fecha de caducidad es demasiado antigua.";
        if (expiresOn.Value > Today.AddYears(10)) return "La fecha de caducidad es demasiado lejana.";
        return null;
    }

    /// <summary>
    /// Acompaña un cambio de stock: entradas con fecha crean lote; salidas descuentan FEFO;
    /// devoluciones (entradas sin fecha) regresan piezas al lote que caduca primero.
    /// No guarda: lo hace quien llama, junto con el movimiento.
    /// </summary>
    public async Task ApplyAsync(
        Guid organizationId,
        int variantId,
        int delta,
        DateOnly? expiresOn,
        string? lotCode,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (delta == 0)
            return;

        if (delta > 0 && expiresOn.HasValue)
        {
            _context.InventoryLots.Add(new InventoryLot
            {
                OrganizationId = organizationId,
                ProductVariantId = variantId,
                ExpiresOn = expiresOn.Value,
                LotCode = string.IsNullOrWhiteSpace(lotCode) ? null : lotCode.Trim()[..Math.Min(lotCode.Trim().Length, 50)],
                QuantityReceived = delta,
                QuantityRemaining = delta,
                Source = source,
                ReceivedAt = DateTime.UtcNow
            });
            return;
        }

        var lots = await _context.InventoryLots
            .Where(x => x.OrganizationId == organizationId && x.ProductVariantId == variantId && x.WrittenOffAt == null)
            .OrderBy(x => x.ExpiresOn)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (lots.Count == 0)
            return;

        if (delta < 0)
        {
            var pending = -delta;
            foreach (var lot in lots.Where(x => x.QuantityRemaining > 0))
            {
                var take = Math.Min(pending, lot.QuantityRemaining);
                lot.QuantityRemaining -= take;
                pending -= take;
                if (pending == 0) break;
            }
        }
        else
        {
            var pending = delta;
            foreach (var lot in lots.Where(x => x.QuantityRemaining < x.QuantityReceived))
            {
                var give = Math.Min(pending, lot.QuantityReceived - lot.QuantityRemaining);
                lot.QuantityRemaining += give;
                pending -= give;
                if (pending == 0) break;
            }
        }
    }

    /// <summary>Lotes con piezas que caducan dentro de <paramref name="withinDays"/> días (incluye los ya caducados).</summary>
    public async Task<IReadOnlyList<ExpiringLot>> GetExpiringAsync(
        Guid organizationId,
        int withinDays = WarningDays,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var today = Today;
        var limit = today.AddDays(withinDays);
        var lots = await _context.InventoryLots
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.WrittenOffAt == null &&
                        x.QuantityRemaining > 0 && x.ExpiresOn <= limit)
            .OrderBy(x => x.ExpiresOn)
            .Take(take)
            .Select(x => new
            {
                x.Id, x.ProductVariantId, x.ProductVariant.ProductId, x.ProductVariant.Sku,
                x.ProductVariant.Product.Title, x.LotCode, x.ExpiresOn, x.QuantityRemaining
            })
            .ToListAsync(cancellationToken);

        return lots.Select(x => new ExpiringLot(
            x.Id, x.ProductVariantId, x.ProductId, x.Sku, x.Title, x.LotCode, x.ExpiresOn, x.QuantityRemaining,
            x.ExpiresOn.DayNumber - today.DayNumber)).ToList();
    }

    /// <summary>Próxima caducidad con piezas por SKU.</summary>
    public async Task<Dictionary<int, (DateOnly ExpiresOn, int Units, int LotUnits)>> GetNextExpiryByVariantAsync(
        Guid organizationId,
        IReadOnlyCollection<int> variantIds,
        CancellationToken cancellationToken = default)
    {
        var lots = await _context.InventoryLots
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.WrittenOffAt == null &&
                        x.QuantityRemaining > 0 && variantIds.Contains(x.ProductVariantId))
            .Select(x => new { x.ProductVariantId, x.ExpiresOn, x.QuantityRemaining })
            .ToListAsync(cancellationToken);

        return lots.GroupBy(x => x.ProductVariantId).ToDictionary(
            g => g.Key,
            g =>
            {
                var first = g.OrderBy(x => x.ExpiresOn).First();
                return (first.ExpiresOn, g.Where(x => x.ExpiresOn == first.ExpiresOn).Sum(x => x.QuantityRemaining), g.Sum(x => x.QuantityRemaining));
            });
    }
}
