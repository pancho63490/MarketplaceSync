using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.Inventory;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Automation;
using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Models.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Automation;

/// <summary>
/// Lista de compra de la semana: productos tuyos que se venden y se están acabando (resurtido) y, si se pide,
/// hasta 3 productos nuevos de "Qué comprar". Se aprueba y se marca como recibida desde Telegram.
/// </summary>
public sealed class WeeklyPurchaseService
{
    public const int MaxNew = 3;
    public const int MaxRestock = 15;

    private readonly AppDbContext _context;
    private readonly IPurchaseRadarService _radar;
    private readonly InventoryLotService _lots;
    private readonly IStockSyncScheduler _stockSync;
    private readonly ILogger<WeeklyPurchaseService> _logger;

    public WeeklyPurchaseService(
        AppDbContext context,
        IPurchaseRadarService radar,
        InventoryLotService lots,
        IStockSyncScheduler stockSync,
        ILogger<WeeklyPurchaseService> logger)
    {
        _context = context;
        _radar = radar;
        _lots = lots;
        _stockSync = stockSync;
        _logger = logger;
    }

    /// <summary>Arma y guarda la lista propuesta; null si no hay nada que comprar.</summary>
    public async Task<PurchaseList?> BuildAsync(Guid org, bool includeNew, CancellationToken cancellationToken = default)
    {
        var list = new PurchaseList { OrganizationId = org };

        var restock = await _radar.GetRestockSuggestionsAsync(org, cancellationToken);
        foreach (var r in restock.Where(r => r.SuggestedQuantity > 0).Take(MaxRestock))
        {
            list.Items.Add(new PurchaseListItem
            {
                Kind = "restock",
                ProductVariantId = r.ProductVariantId,
                Title = Cut(r.Title, 300),
                Sku = r.Sku,
                Quantity = r.SuggestedQuantity,
                UnitCostUsd = string.Equals(r.SourceCurrency, "MXN", StringComparison.OrdinalIgnoreCase) ? null : r.SourcePrice,
                Url = r.SourceUrl is { Length: <= 1000 } ? r.SourceUrl : null,
                Reason = $"vendió {r.UnitsSold30Days} en 30 días, quedan {r.CurrentStock}"
            });
        }

        if (includeNew)
        {
            var candidates = await _context.PurchaseCandidates.AsNoTracking()
                .Where(c => c.OrganizationId == org && c.Status == PurchaseCandidateStatus.Suggested &&
                            c.SourcePriceUsd > 0 && c.SuggestedQuantity > 0)
                .OrderByDescending(c => c.DemandScore)
                .Take(MaxNew)
                .ToListAsync(cancellationToken);
            foreach (var c in candidates)
            {
                list.Items.Add(new PurchaseListItem
                {
                    Kind = "new",
                    CandidateId = c.Id,
                    Title = Cut(c.SourceTitle ?? c.MarketName, 300),
                    Quantity = c.SuggestedQuantity,
                    UnitCostUsd = c.SourcePriceUsd,
                    Url = c.SourceUrl is { Length: <= 1000 } ? c.SourceUrl : null,
                    Reason = $"nuevo · demanda {c.DemandScore}/100 · gana {(c.EstimatedNetProfitMxn ?? 0).ToString("C0", System.Globalization.CultureInfo.GetCultureInfo("es-MX"))}/pza"
                });
            }
        }

        if (list.Items.Count == 0)
            return null;

        list.TotalUsd = list.Items.Sum(i => (i.UnitCostUsd ?? 0) * i.Quantity);
        // La lista anterior que nadie contestó queda como omitida.
        await _context.PurchaseLists
            .Where(l => l.OrganizationId == org && l.Status == "proposed")
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, "skipped"), cancellationToken);
        _context.PurchaseLists.Add(list);
        await _context.SaveChangesAsync(cancellationToken);
        return list;
    }

    public Task<PurchaseList?> LoadAsync(Guid org, int listId, CancellationToken cancellationToken = default) =>
        _context.PurchaseLists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == listId && l.OrganizationId == org, cancellationToken);

    /// <summary>Aprobar: los productos nuevos quedan "en camino" (producto, SKU y borrador listos).</summary>
    public async Task<string> ApproveAsync(Guid org, int listId, CancellationToken cancellationToken = default)
    {
        var list = await LoadAsync(org, listId, cancellationToken);
        if (list is null) return "Ya no encontré esa lista.";
        if (list.Status != "proposed") return list.Status == "approved" ? "Esa lista ya estaba aprobada." : "Esa lista ya no está vigente.";

        var problems = new List<string>();
        foreach (var item in list.Items.Where(i => i.Kind == "new" && i.CandidateId.HasValue))
        {
            try
            {
                await _radar.MarkPurchasedAsync(org, item.CandidateId!.Value, item.Quantity, item.UnitCostUsd, null, cancellationToken);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add($"{Cut(item.Title, 40)}: {exception.Message}");
            }
        }

        list.Status = "approved";
        list.ApprovedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return problems.Count == 0 ? "Aprobada." : "Aprobada, con avisos: " + string.Join(" · ", problems);
    }

    public async Task<string> SkipAsync(Guid org, int listId, CancellationToken cancellationToken = default)
    {
        var list = await LoadAsync(org, listId, cancellationToken);
        if (list is null || list.Status != "proposed") return "Esa lista ya no está vigente.";
        list.Status = "skipped";
        await _context.SaveChangesAsync(cancellationToken);
        return "Omitida esta semana.";
    }

    /// <summary>Ya llegó a Juárez: suma el stock de todo y lo manda a Mercado Libre.</summary>
    public async Task<string> ReceiveAsync(Guid org, int listId, CancellationToken cancellationToken = default)
    {
        var list = await LoadAsync(org, listId, cancellationToken);
        if (list is null) return "Ya no encontré esa lista.";
        if (list.Status == "received") return "Esa compra ya estaba recibida.";
        if (list.Status != "approved") return "Primero hay que aprobar la lista.";

        var productIds = new HashSet<int>();
        var problems = new List<string>();
        foreach (var item in list.Items)
        {
            try
            {
                if (item.Kind == "new" && item.CandidateId.HasValue)
                {
                    await _radar.MarkReceivedAsync(org, item.CandidateId.Value, item.Quantity, null, null, cancellationToken);
                    continue;
                }

                var variant = await _context.ProductVariants
                    .FirstOrDefaultAsync(v => v.Id == item.ProductVariantId && v.OrganizationId == org, cancellationToken);
                if (variant is null)
                {
                    problems.Add($"{Cut(item.Title, 40)}: ya no existe el SKU");
                    continue;
                }

                variant.Stock += item.Quantity;
                variant.UpdatedAt = DateTime.UtcNow;
                await _lots.ApplyAsync(org, variant.Id, item.Quantity, null, null, "Compra", cancellationToken);
                _context.InventoryMovements.Add(new InventoryMovement
                {
                    OrganizationId = org,
                    ProductVariantId = variant.Id,
                    Type = "Purchase",
                    QuantityDelta = item.Quantity,
                    StockAfter = variant.Stock,
                    Notes = $"Compra semanal #{list.Id} recibida (Telegram)",
                    CreatedAt = DateTime.UtcNow
                });
                productIds.Add(variant.ProductId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Receiving purchase list item {ItemId} failed.", item.Id);
                problems.Add($"{Cut(item.Title, 40)}: {exception.Message}");
            }
        }

        list.Status = "received";
        list.ReceivedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        if (productIds.Count > 0)
            _stockSync.RequestPush(productIds);
        return problems.Count == 0
            ? "Recibida: el stock ya se sumó y se manda a Mercado Libre."
            : "Recibida, con avisos: " + string.Join(" · ", problems);
    }

    private static string Cut(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "(sin título)" : value.Length <= max ? value : value[..max];
}
