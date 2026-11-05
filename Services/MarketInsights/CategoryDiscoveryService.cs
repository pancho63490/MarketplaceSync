using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.MarketInsights;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MarketInsights;

public sealed record MlCategoryNode(string Id, string Name, int? TotalItems, bool HasChildren);

public sealed record MlCategoryInfo(
    string Id,
    string Name,
    string PathText,
    IReadOnlyList<MlCategoryNode> Children);

public sealed record DiscoveryScanResult(int Scanned, int WithRanking, int Failed, string? Warning);

/// <summary>
/// Explora sola las categorías que el vendedor eligió: las expande a sus subcategorías finales
/// y cada día lee los más vendidos de las que llevan más tiempo sin revisarse. Así el Radar de compra
/// encuentra productos nuevos sin que nadie tenga que buscarlos.
/// </summary>
public sealed class CategoryDiscoveryService
{
    /// <summary>Accesorios para Vehículos en Mercado Libre México.</summary>
    public const string VehicleAccessoriesRootId = "MLM1747";

    public const int MaxLeavesPerSelection = 80;
    public const int MaxCategoriesPerDailyScan = 60;
    private const int MaxDepth = 4;
    private static readonly TimeSpan ReExpandEvery = TimeSpan.FromDays(7);
    private static readonly TimeSpan DelayBetweenCategories = TimeSpan.FromMilliseconds(250);

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMercadoLibreAuthService _authService;
    private readonly IMercadoLibreBestSellerService _bestSellers;
    private readonly IMarketOpportunityAnalyzer _analyzer;
    private readonly IMarketRankingHistoryService _history;
    private readonly ILogger<CategoryDiscoveryService> _logger;
    private readonly string _apiBase;

    public CategoryDiscoveryService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IMercadoLibreAuthService authService,
        IMercadoLibreBestSellerService bestSellers,
        IMarketOpportunityAnalyzer analyzer,
        IMarketRankingHistoryService history,
        IConfiguration configuration,
        ILogger<CategoryDiscoveryService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _bestSellers = bestSellers;
        _analyzer = analyzer;
        _history = history;
        _logger = logger;
        var configured = configuration["MercadoLibre:ApiBaseUrl"];
        _apiBase = string.IsNullOrWhiteSpace(configured) ? "https://api.mercadolibre.com" : configured.TrimEnd('/');
    }

    // ─────────────────────────────────────────────────────────────
    // Árbol de categorías de Mercado Libre
    // ─────────────────────────────────────────────────────────────

    public async Task<MlCategoryInfo> GetCategoryAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        categoryId = NormalizeId(categoryId);
        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync($"{_apiBase}/categories/{Uri.EscapeDataString(categoryId)}", cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Mercado Libre no encontró la categoría {categoryId}.", null, response.StatusCode);

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var path = root.TryGetProperty("path_from_root", out var p) && p.ValueKind == JsonValueKind.Array
            ? string.Join(" › ", p.EnumerateArray().Select(x => ReadString(x, "name")).Where(x => !string.IsNullOrWhiteSpace(x)))
            : ReadString(root, "name") ?? categoryId;

        var children = new List<MlCategoryNode>();
        if (root.TryGetProperty("children_categories", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in list.EnumerateArray())
            {
                var id = ReadString(child, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                int? total = child.TryGetProperty("total_items_in_this_category", out var t) && t.TryGetInt32(out var n) ? n : null;
                // El listado de hijos no dice si tienen hijos; se sabrá al expandir.
                children.Add(new MlCategoryNode(id, ReadString(child, "name") ?? id, total, true));
            }
        }

        return new MlCategoryInfo(categoryId, ReadString(root, "name") ?? categoryId, path, children);
    }

    // ─────────────────────────────────────────────────────────────
    // Elegir / quitar
    // ─────────────────────────────────────────────────────────────

    /// <summary>Agrega una categoría elegida y la expande a sus subcategorías finales.</summary>
    public async Task<WatchedCategory> AddAsync(Guid organizationId, string categoryId, CancellationToken cancellationToken = default)
    {
        var info = await GetCategoryAsync(categoryId, cancellationToken);

        var watched = await _context.WatchedCategories
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.CategoryId == info.Id, cancellationToken);
        if (watched is null)
        {
            watched = new WatchedCategory { OrganizationId = organizationId, CategoryId = info.Id };
            _context.WatchedCategories.Add(watched);
        }

        watched.Name = Truncate(info.Name, 300)!;
        watched.PathText = Truncate(info.PathText, 600);
        watched.IsSelected = true;
        watched.ParentWatchedCategoryId = null;
        watched.Enabled = true;
        await _context.SaveChangesAsync(cancellationToken);

        await ExpandAsync(watched, info, cancellationToken);
        return watched;
    }

    public async Task RemoveAsync(Guid organizationId, int watchedCategoryId, CancellationToken cancellationToken = default)
    {
        var watched = await _context.WatchedCategories
            .FirstOrDefaultAsync(x => x.Id == watchedCategoryId && x.OrganizationId == organizationId && x.IsSelected, cancellationToken)
            ?? throw new KeyNotFoundException("No se encontró la categoría.");

        var leaves = await _context.WatchedCategories
            .Where(x => x.ParentWatchedCategoryId == watched.Id)
            .ToListAsync(cancellationToken);
        _context.WatchedCategories.RemoveRange(leaves);
        _context.WatchedCategories.Remove(watched);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(Guid organizationId, int watchedCategoryId, bool enabled, CancellationToken cancellationToken = default)
    {
        var watched = await _context.WatchedCategories
            .FirstOrDefaultAsync(x => x.Id == watchedCategoryId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("No se encontró la categoría.");
        watched.Enabled = enabled;
        if (watched.IsSelected)
        {
            await _context.WatchedCategories
                .Where(x => x.ParentWatchedCategoryId == watched.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Enabled, enabled), cancellationToken);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Recorre el árbol hasta las categorías finales (sin hijos), que son las que tienen ranking más preciso.
    /// Si la elegida ya es final, se revisa ella misma.
    /// </summary>
    private async Task ExpandAsync(WatchedCategory selected, MlCategoryInfo? info, CancellationToken cancellationToken)
    {
        info ??= await GetCategoryAsync(selected.CategoryId, cancellationToken);

        var leaves = new List<(string Id, string Name, string Path)>();
        var queue = new Queue<(MlCategoryInfo Info, int Depth)>();
        queue.Enqueue((info, 0));

        while (queue.Count > 0 && leaves.Count < MaxLeavesPerSelection)
        {
            var (current, depth) = queue.Dequeue();
            if (current.Children.Count == 0 || depth >= MaxDepth)
            {
                leaves.Add((current.Id, current.Name, current.PathText));
                continue;
            }

            foreach (var child in current.Children
                         .OrderByDescending(c => c.TotalItems ?? 0)) // primero las más grandes
            {
                if (leaves.Count + queue.Count >= MaxLeavesPerSelection * 2)
                    break;
                try
                {
                    var childInfo = await GetCategoryAsync(child.Id, cancellationToken);
                    queue.Enqueue((childInfo, depth + 1));
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(exception, "Could not expand category {CategoryId}.", child.Id);
                }
            }
        }

        selected.IsLeaf = leaves.Count == 1 && leaves[0].Id.Equals(selected.CategoryId, StringComparison.OrdinalIgnoreCase);
        selected.LastExpandedAt = DateTime.UtcNow;

        var existing = await _context.WatchedCategories
            .Where(x => x.OrganizationId == selected.OrganizationId)
            .ToListAsync(cancellationToken);
        var byId = existing.ToDictionary(x => x.CategoryId, StringComparer.OrdinalIgnoreCase);

        foreach (var leaf in leaves.Take(MaxLeavesPerSelection))
        {
            if (leaf.Id.Equals(selected.CategoryId, StringComparison.OrdinalIgnoreCase))
                continue; // la elegida misma (IsLeaf = true)

            if (!byId.TryGetValue(leaf.Id, out var watched))
            {
                watched = new WatchedCategory
                {
                    OrganizationId = selected.OrganizationId,
                    CategoryId = leaf.Id,
                    ParentWatchedCategoryId = selected.Id,
                    Enabled = selected.Enabled
                };
                _context.WatchedCategories.Add(watched);
                byId[leaf.Id] = watched;
            }

            watched.Name = Truncate(leaf.Name, 300)!;
            watched.PathText = Truncate(leaf.Path, 600);
            watched.IsLeaf = true;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────
    // Exploración diaria
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lee los más vendidos de las subcategorías que llevan más tiempo sin revisar y guarda el historial
    /// que usa el Radar de compra.
    /// </summary>
    public async Task<DiscoveryScanResult> ScanOrganizationAsync(
        Guid organizationId,
        int maxCategories = MaxCategoriesPerDailyScan,
        CancellationToken cancellationToken = default)
    {
        // Re-expande las elegidas cada semana (Mercado Libre agrega y cambia subcategorías).
        var selections = await _context.WatchedCategories
            .Where(x => x.OrganizationId == organizationId && x.IsSelected && x.Enabled &&
                        (x.LastExpandedAt == null || x.LastExpandedAt < DateTime.UtcNow - ReExpandEvery))
            .ToListAsync(cancellationToken);
        foreach (var selection in selections)
        {
            try
            {
                await ExpandAsync(selection, null, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Could not re-expand category {CategoryId}.", selection.CategoryId);
            }
        }

        var account = await _context.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre" && x.Status == "Connected")
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id })
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
            return new DiscoveryScanResult(0, 0, 0, "Conecta tu cuenta de Mercado Libre para explorar categorías.");

        string token;
        try
        {
            token = await _authService.GetValidAccessTokenAsync(organizationId, account.Id);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Discovery could not get a token for organization {OrganizationId}.", organizationId);
            return new DiscoveryScanResult(0, 0, 0, "No se pudo renovar el acceso a Mercado Libre. Vuelve a conectar tu cuenta.");
        }

        // Las que se revisan: finales (o elegidas que son finales), primero las nunca revisadas.
        var toScan = await _context.WatchedCategories
            .Where(x => x.OrganizationId == organizationId && x.Enabled && x.IsLeaf)
            .OrderBy(x => x.LastScannedAt ?? DateTime.MinValue)
            .Take(maxCategories)
            .ToListAsync(cancellationToken);

        var existingIds = await _context.MarketplacePublications
            .AsNoTracking()
            .Where(x => x.Product.OrganizationId == organizationId && x.ExternalItemId != null)
            .Select(x => x.ExternalItemId!)
            .ToListAsync(cancellationToken);
        var existingSet = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        int scanned = 0, withRanking = 0, failed = 0;
        var capturedToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in toScan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            try
            {
                var ranking = await _bestSellers.GetTopAsync(category.CategoryId, token, 10, cancellationToken);
                category.LastItemsFound = ranking.Items.Count;
                category.LastError = null;

                // Si la subcategoría no tiene ranking se usa el de su categoría superior: se guarda una sola vez por día.
                if (ranking.Items.Count > 0 && capturedToday.Add(ranking.CategoryId))
                {
                    var analysis = _analyzer.Analyze(ranking, existingSet);
                    await _history.CaptureAsync(organizationId, account.Id, ranking, analysis, cancellationToken);
                    withRanking++;
                }
                else if (!ranking.CategoryId.Equals(category.CategoryId, StringComparison.OrdinalIgnoreCase))
                {
                    category.LastError = "Usa el ranking de su categoría superior";
                }
            }
            catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                category.LastItemsFound = 0;
                category.LastError = "Mercado Libre no publica más vendidos aquí";
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                category.LastError = Truncate(exception.Message, 300);
                _logger.LogWarning(exception, "Discovery scan failed for category {CategoryId}.", category.CategoryId);
            }

            category.LastScannedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await Task.Delay(DelayBetweenCategories, cancellationToken);
        }

        return new DiscoveryScanResult(scanned, withRanking, failed,
            toScan.Count == 0 ? "Elige al menos una categoría para que la app empiece a explorar." : null);
    }

    public async Task<IReadOnlyList<Guid>> GetOrganizationsWithCategoriesAsync(CancellationToken cancellationToken = default) =>
        await _context.WatchedCategories
            .AsNoTracking()
            .Where(x => x.Enabled)
            .Select(x => x.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);

    // ─────────────────────────────────────────────────────────────

    private static string NormalizeId(string id) => id.Trim().ToUpperInvariant();

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
