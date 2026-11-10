using System.Globalization;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Models.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Purchasing;

/// <summary>Costo de cada renglón del viaje.</summary>
public sealed record TripLineCost(
    SupplyTripItem Item, string? Sku, decimal LineUsd, decimal LineMxn, decimal ExtrasShareMxn,
    decimal RealUnitMxn, decimal EstimatedUnitMxn)
{
    public decimal DifferencePercent => EstimatedUnitMxn > 0 ? Math.Round((RealUnitMxn - EstimatedUnitMxn) / EstimatedUnitMxn * 100m, 1) : 0;
}

public sealed class TripBreakdown
{
    public required SupplyTrip Trip { get; init; }
    public List<TripLineCost> Lines { get; init; } = new();
    public decimal GoodsUsd => Lines.Sum(l => l.LineUsd);
    public decimal GoodsMxn => Lines.Sum(l => l.LineMxn);
    public decimal TotalMxn => GoodsMxn + Trip.ExtrasMxn;
    public decimal EstimatedMxn => Lines.Sum(l => l.EstimatedUnitMxn * l.Item.Quantity);
    public int Pieces => Lines.Sum(l => l.Item.Quantity);

    /// <summary>Gastos del viaje (gasolina, cruce, impuestos, otros) como % del valor de la mercancía.</summary>
    public decimal RealCrossingPercent => GoodsMxn > 0 ? Math.Round(Trip.ExtrasMxn / GoodsMxn * 100m, 1) : 0;

    /// <summary>% de cruce que tienes en Costos de cruce (para comparar).</summary>
    public decimal SettingsPercent { get; init; }
}

/// <summary>
/// Viajes a El Paso: lo que compraste (en dólares, con su envío a El Paso) y lo que costó traerlo
/// (gasolina, cruce o comercializadora, impuestos, otros). Al recibir, reparte esos gastos entre las piezas
/// según su valor, guarda el costo real por pieza en el producto (y la ganancia se calcula con él) y suma
/// las piezas al inventario.
/// </summary>
public sealed class SupplyTripService
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly ICrossBorderPricingService _pricing;
    private readonly IStockSyncScheduler _stockSync;

    public SupplyTripService(AppDbContext context, ICrossBorderPricingService pricing, IStockSyncScheduler stockSync)
    {
        _context = context;
        _pricing = pricing;
        _stockSync = stockSync;
    }

    public static DateOnly TodayJuarez => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));

    public Task<SupplyTrip?> LoadAsync(Guid org, int id, CancellationToken cancellationToken = default) =>
        _context.SupplyTrips.Include(t => t.Items).FirstOrDefaultAsync(t => t.Id == id && t.OrganizationId == org, cancellationToken);

    public async Task<SupplyTrip> CreateAsync(Guid org, string? name, DateOnly? date, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var day = date ?? TodayJuarez;
        var trip = new SupplyTrip
        {
            OrganizationId = org,
            Name = string.IsNullOrWhiteSpace(name) ? $"Viaje del {day.ToString("d 'de' MMMM", Mx)}" : name.Trim()[..Math.Min(name.Trim().Length, 120)],
            Date = day,
            ExchangeRate = settings.UsdToMxnRate > 0 ? settings.UsdToMxnRate : 20m
        };
        _context.SupplyTrips.Add(trip);
        await _context.SaveChangesAsync(cancellationToken);
        return trip;
    }

    public async Task<(bool Ok, string Message)> AddItemAsync(Guid org, int tripId, string? product, int quantity, decimal unitCostUsd, decimal shippingUsd, string? store, CancellationToken cancellationToken = default)
    {
        var trip = await LoadAsync(org, tripId, cancellationToken);
        if (trip is null) return (false, "No encontré ese viaje.");
        if (quantity is < 1 or > 10_000) return (false, "Escribe cuántas piezas (1 o más).");
        if (unitCostUsd <= 0) return (false, "Escribe lo que costó cada pieza en dólares.");
        if (string.IsNullOrWhiteSpace(product)) return (false, "Escribe qué compraste (SKU o descripción).");

        // "SKU · título" del autocompletado, o solo el SKU.
        var text = product.Trim();
        var sku = text.Split(" · ", 2)[0].Trim();
        var variant = await _context.ProductVariants.AsNoTracking()
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.OrganizationId == org && v.IsActive && v.Sku == sku, cancellationToken);

        trip.Items.Add(new SupplyTripItem
        {
            ProductVariantId = variant?.Id,
            Description = variant?.Product.Title ?? text[..Math.Min(text.Length, 300)],
            Quantity = quantity,
            UnitCostUsd = Math.Round(unitCostUsd, 2),
            ShippingUsd = Math.Max(0, Math.Round(shippingUsd, 2)),
            Store = string.IsNullOrWhiteSpace(store) ? null : store.Trim()[..Math.Min(store.Trim().Length, 60)]
        });
        await _context.SaveChangesAsync(cancellationToken);
        return (true, variant is null
            ? $"Agregué «{Short(text, 40)}». No es un SKU de la app: su costo real no se guarda en un producto."
            : $"Agregué {quantity} × {variant.Sku}.");
    }

    /// <summary>El viaje que está abierto (comprando), el más reciente.</summary>
    public Task<SupplyTrip?> OpenTripAsync(Guid org, CancellationToken cancellationToken = default) =>
        _context.SupplyTrips.Include(t => t.Items)
            .Where(t => t.OrganizationId == org && t.Status == SupplyTripStatus.Planning)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Agrega al viaje un producto mandado por link (Telegram). Si el producto tiene un solo SKU activo se liga a él
    /// para sumar inventario al recibir; si no, se liga al producto para guardarle el costo real.
    /// El precio puede quedar en 0: la pantalla del viaje lo marca y no deja recibir hasta escribirlo.
    /// </summary>
    public async Task<SupplyTripItem> AddFromLinkAsync(Guid org, SupplyTrip trip, Product product, string link, int quantity, decimal? unitCostUsd, CancellationToken cancellationToken = default)
    {
        var variants = await _context.ProductVariants.AsNoTracking()
            .Where(v => v.OrganizationId == org && v.ProductId == product.Id && v.IsActive)
            .Select(v => v.Id).Take(2).ToListAsync(cancellationToken);
        var unit = unitCostUsd is > 0 ? unitCostUsd.Value
            : product.SourcePrice is > 0 && string.Equals(product.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase) ? product.SourcePrice.Value : 0m;
        var title = string.IsNullOrWhiteSpace(product.Title) ? "Producto sin título" : product.Title.Trim();
        var item = new SupplyTripItem
        {
            SupplyTripId = trip.Id,
            ProductId = product.Id,
            ProductVariantId = variants.Count == 1 ? variants[0] : null,
            Description = title[..Math.Min(title.Length, 300)],
            Quantity = Math.Clamp(quantity, 1, 10_000),
            UnitCostUsd = Math.Round(unit, 2),
            Store = StoreName(product.SourceMarketplace, link),
            SourceUrl = link.Length <= 1000 ? link : null
        };
        _context.SupplyTripItems.Add(item);
        await _context.SaveChangesAsync(cancellationToken);
        return item;
    }

    private static string? StoreName(string? marketplace, string link)
    {
        var known = (marketplace ?? string.Empty).ToLowerInvariant() switch
        {
            "ebay" => "eBay",
            "amazon" => "Amazon",
            "walmart" => "Walmart",
            "aliexpress" => "AliExpress",
            "rockauto" => "RockAuto",
            _ => null
        };
        if (known is not null) return known;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", string.Empty)[..Math.Min(uri.Host.Replace("www.", string.Empty).Length, 60)] : null;
    }

    public async Task<bool> RemoveItemAsync(Guid org, int tripId, int itemId, CancellationToken cancellationToken = default)
    {
        var trip = await LoadAsync(org, tripId, cancellationToken);
        var item = trip?.Items.FirstOrDefault(i => i.Id == itemId);
        if (trip is null || item is null || trip.Status == SupplyTripStatus.Received) return false;
        _context.SupplyTripItems.Remove(item);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Trae las piezas de la lista de compra semanal aprobada que aún no se recibe.</summary>
    public async Task<(bool Ok, string Message)> ImportPurchaseListAsync(Guid org, int tripId, CancellationToken cancellationToken = default)
    {
        var trip = await LoadAsync(org, tripId, cancellationToken);
        if (trip is null) return (false, "No encontré ese viaje.");
        var list = await _context.PurchaseLists.Include(l => l.Items)
            .Where(l => l.OrganizationId == org && l.Status == "approved")
            .OrderByDescending(l => l.ApprovedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (list is null) return (false, "No hay una lista de compra aprobada pendiente de recibir.");
        if (trip.PurchaseListId == list.Id) return (false, "Esa lista ya está en el viaje.");

        var added = 0;
        foreach (var item in list.Items.Where(i => i.Quantity > 0))
        {
            trip.Items.Add(new SupplyTripItem
            {
                ProductVariantId = item.ProductVariantId,
                Description = item.Title,
                Quantity = item.Quantity,
                UnitCostUsd = item.UnitCostUsd ?? 0,
                Store = item.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", string.Empty) : null
            });
            added++;
        }
        trip.PurchaseListId = list.Id;
        await _context.SaveChangesAsync(cancellationToken);
        var noPrice = trip.Items.Count(i => i.UnitCostUsd <= 0);
        return (true, $"Agregué {added} {(added == 1 ? "pieza" : "piezas")} de la lista de compra." + (noPrice > 0 ? $" Escribe el precio real de {noPrice}." : ""));
    }

    public async Task<(bool Ok, string Message)> UpdateCostsAsync(Guid org, int tripId, string? name, DateOnly? date, decimal exchangeRate,
        decimal fuel, decimal crossing, decimal taxes, decimal other, string? notes, IDictionary<int, (decimal Unit, decimal Shipping, int Qty)>? items,
        CancellationToken cancellationToken = default)
    {
        var trip = await LoadAsync(org, tripId, cancellationToken);
        if (trip is null) return (false, "No encontré ese viaje.");
        if (exchangeRate is < 10 or > 40) return (false, "El tipo de cambio no se ve bien (debe estar entre 10 y 40).");
        if (!string.IsNullOrWhiteSpace(name)) trip.Name = name.Trim()[..Math.Min(name.Trim().Length, 120)];
        if (date is { } d) trip.Date = d;
        trip.ExchangeRate = Math.Round(exchangeRate, 4);
        trip.FuelMxn = Math.Max(0, fuel);
        trip.CrossingMxn = Math.Max(0, crossing);
        trip.CustomsTaxesMxn = Math.Max(0, taxes);
        trip.OtherMxn = Math.Max(0, other);
        trip.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()[..Math.Min(notes.Trim().Length, 1000)];
        if (items is not null && trip.Status != SupplyTripStatus.Received)
            foreach (var item in trip.Items)
                if (items.TryGetValue(item.Id, out var v))
                {
                    if (v.Unit > 0) item.UnitCostUsd = Math.Round(v.Unit, 2);
                    item.ShippingUsd = Math.Max(0, Math.Round(v.Shipping, 2));
                    if (v.Qty > 0) item.Quantity = v.Qty;
                }
        await _context.SaveChangesAsync(cancellationToken);

        // Si ya se recibió, se vuelve a calcular el costo real con los gastos corregidos.
        if (trip.Status == SupplyTripStatus.Received)
            await ApplyRealCostsAsync(org, trip, cancellationToken);
        return (true, trip.Status == SupplyTripStatus.Received ? "Guardé los gastos y recalculé el costo real de las piezas." : "Guardé el viaje.");
    }

    // ─────────────────────────────────────────────────────────────
    // Costo real
    // ─────────────────────────────────────────────────────────────

    public async Task<TripBreakdown> BreakdownAsync(Guid org, SupplyTrip trip, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var variantIds = trip.Items.Where(i => i.ProductVariantId != null).Select(i => i.ProductVariantId!.Value).ToList();
        var skus = await _context.ProductVariants.AsNoTracking()
            .Where(v => variantIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.Sku, cancellationToken);

        var rate = trip.ExchangeRate > 0 ? trip.ExchangeRate : settings.UsdToMxnRate;
        var lines = trip.Items.OrderBy(i => i.Id).Select(i =>
        {
            var lineUsd = i.UnitCostUsd * i.Quantity + i.ShippingUsd;
            return (Item: i, LineUsd: lineUsd, LineMxn: Math.Round(lineUsd * rate, 2));
        }).ToList();
        var goods = lines.Sum(l => l.LineMxn);

        var result = lines.Select(l =>
        {
            var share = goods > 0 ? Math.Round(trip.ExtrasMxn * l.LineMxn / goods, 2) : 0;
            var qty = Math.Max(1, l.Item.Quantity);
            var real = Math.Round((l.LineMxn + share) / qty, 2);
            var unitUsd = l.Item.UnitCostUsd + l.Item.ShippingUsd / qty;
            var estimated = _pricing.LandedCostMxn(unitUsd, "USD", settings);
            return new TripLineCost(l.Item, l.Item.ProductVariantId is { } vid && skus.TryGetValue(vid, out var sku) ? sku : null,
                l.LineUsd, l.LineMxn, share, real, estimated);
        }).ToList();

        return new TripBreakdown { Trip = trip, Lines = result, SettingsPercent = settings.ImportFeePercent };
    }

    /// <summary>Recibe el viaje en Juárez: guarda el costo real por pieza y (si se pide) suma las piezas al inventario.</summary>
    public async Task<(bool Ok, string Message)> ReceiveAsync(Guid org, int tripId, bool addToInventory, CancellationToken cancellationToken = default)
    {
        var trip = await LoadAsync(org, tripId, cancellationToken);
        if (trip is null) return (false, "No encontré ese viaje.");
        if (trip.Status == SupplyTripStatus.Received) return (false, "Ese viaje ya estaba recibido.");
        if (trip.Items.Count == 0) return (false, "El viaje no tiene piezas.");
        if (trip.Items.Any(i => i.UnitCostUsd <= 0)) return (false, "Falta el precio en dólares de alguna pieza.");

        var products = await ApplyRealCostsAsync(org, trip, cancellationToken);
        var pieces = 0;
        if (addToInventory)
        {
            // Piezas que llegaron por link sin SKU: si su producto ya tiene un solo SKU, se suman a ese.
            foreach (var item in trip.Items.Where(i => i.ProductVariantId == null && i.ProductId != null))
            {
                var ids = await _context.ProductVariants.AsNoTracking()
                    .Where(v => v.OrganizationId == org && v.ProductId == item.ProductId && v.IsActive)
                    .Select(v => v.Id).Take(2).ToListAsync(cancellationToken);
                if (ids.Count == 1) item.ProductVariantId = ids[0];
            }
            foreach (var item in trip.Items.Where(i => i.ProductVariantId != null))
            {
                var variant = await _context.ProductVariants.FirstOrDefaultAsync(v => v.Id == item.ProductVariantId && v.OrganizationId == org, cancellationToken);
                if (variant is null) continue;
                variant.Stock += item.Quantity;
                variant.UpdatedAt = DateTime.UtcNow;
                pieces += item.Quantity;
                _context.InventoryMovements.Add(new InventoryMovement
                {
                    OrganizationId = org,
                    ProductVariantId = variant.Id,
                    Type = "PurchaseReceipt",
                    QuantityDelta = item.Quantity,
                    StockAfter = variant.Stock,
                    Notes = $"{trip.Name} (costo real {item.RealUnitCostMxn?.ToString("C2", Mx)} c/u)",
                    CreatedAt = DateTime.UtcNow
                });
                products.Add(variant.ProductId);
            }
        }

        trip.Status = SupplyTripStatus.Received;
        trip.ReceivedAt = DateTime.UtcNow;
        if (trip.PurchaseListId is { } listId)
        {
            var list = await _context.PurchaseLists.FirstOrDefaultAsync(l => l.Id == listId && l.OrganizationId == org, cancellationToken);
            if (list is { Status: "approved" })
            {
                list.Status = "received";
                list.ReceivedAt = DateTime.UtcNow;
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        if (products.Count > 0)
            _stockSync.RequestPush(products);

        var breakdown = await BreakdownAsync(org, trip, cancellationToken);
        return (true, $"Viaje recibido: costo real {breakdown.TotalMxn.ToString("C0", Mx)} por {breakdown.Pieces} piezas" +
                      (addToInventory && pieces > 0 ? $"; sumé {pieces} al inventario." : ".") +
                      $" Los gastos del viaje fueron {breakdown.RealCrossingPercent.ToString("0.#", Mx)}% de la mercancía (en Costos de cruce tienes {breakdown.SettingsPercent.ToString("0.#", Mx)}%).");
    }

    /// <summary>Guarda el costo real en cada renglón y en su producto. Regresa los productos tocados.</summary>
    private async Task<HashSet<int>> ApplyRealCostsAsync(Guid org, SupplyTrip trip, CancellationToken cancellationToken)
    {
        var breakdown = await BreakdownAsync(org, trip, cancellationToken);
        var touched = new HashSet<int>();
        foreach (var line in breakdown.Lines)
        {
            line.Item.RealUnitCostMxn = line.RealUnitMxn;
            line.Item.EstimatedUnitCostMxn = line.EstimatedUnitMxn;
            Product? product = null;
            if (line.Item.ProductVariantId is { } variantId)
                product = await _context.ProductVariants.Where(v => v.Id == variantId && v.OrganizationId == org)
                    .Select(v => v.Product).FirstOrDefaultAsync(cancellationToken);
            else if (line.Item.ProductId is { } productId)
                product = await _context.Products.FirstOrDefaultAsync(p => p.Id == productId && p.OrganizationId == org, cancellationToken);
            if (product is null) continue;
            // Un viaje más viejo no pisa el costo de uno más nuevo.
            var newer = product.RealLandedTripId is { } previous && previous != trip.Id &&
                        await _context.SupplyTrips.AnyAsync(t => t.Id == previous && t.Date > trip.Date, cancellationToken);
            if (newer) continue;
            product.RealLandedCostMxn = line.RealUnitMxn;
            product.RealLandedCostAt = DateTime.UtcNow;
            product.RealLandedTripId = trip.Id;
            touched.Add(product.Id);
        }
        await _context.SaveChangesAsync(cancellationToken);
        return touched;
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
