using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.CrossBorder;
using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Models.MarketInsights;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Purchasing;

/// <summary>
/// Radar de compra: decide qué conviene traer de EE.UU. antes de publicar.
///
/// 1. Demanda: usa el historial diario de Más vendidos (constancia en el Top, mejor posición y tendencia).
/// 2. Fuente: busca el producto en eBay EE.UU. (nuevo, precio fijo, vendedor en EE.UU.).
/// 3. Margen: calcula la utilidad vendiendo al precio actual del mercado con las reglas de costeo.
/// 4. Cantidad: sugiere un lote pequeño de prueba según la fuerza de la demanda.
/// </summary>
public sealed class PurchaseRadarService : IPurchaseRadarService
{
    private const int WindowDays = 21;
    private const int MaxCandidates = 40;
    private const int MaxSourceSearchesPerRefresh = 25;
    private const int MinMatchConfidence = 40;
    private const int RestockTargetCoverDays = 21;
    private static readonly TimeSpan SourceSearchCacheTime = TimeSpan.FromHours(24);
    private static readonly TimeSpan DelayBetweenSearches = TimeSpan.FromMilliseconds(300);

    private readonly AppDbContext _context;
    private readonly EbayApiService _ebayApiService;
    private readonly ICrossBorderPricingService _pricingService;
    private readonly SourceSearchQueryBuilder _queryBuilder;
    private readonly ILogger<PurchaseRadarService> _logger;
    private readonly MarketplaceSync.Services.MercadoLibre.IStockSyncScheduler _stockSync;

    public PurchaseRadarService(
        AppDbContext context,
        EbayApiService ebayApiService,
        ICrossBorderPricingService pricingService,
        SourceSearchQueryBuilder queryBuilder,
        ILogger<PurchaseRadarService> logger,
        MarketplaceSync.Services.MercadoLibre.IStockSyncScheduler stockSync)
    {
        _context = context;
        _ebayApiService = ebayApiService;
        _pricingService = pricingService;
        _queryBuilder = queryBuilder;
        _logger = logger;
        _stockSync = stockSync;
    }

    // ─────────────────────────────────────────────────────────────
    // Actualizar radar
    // ─────────────────────────────────────────────────────────────

    public async Task<PurchaseRadarRefreshResult> RefreshAsync(
        Guid organizationId,
        bool forceSourceSearch = false,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-WindowDays);

        var snapshots = await _context.MarketRankingSnapshots
            .AsNoTracking()
            .Include(x => x.Items)
            .Where(x => x.OrganizationId == organizationId && x.SnapshotDate >= from)
            .ToListAsync(cancellationToken);

        if (snapshots.Count == 0)
        {
            return new PurchaseRadarRefreshResult(0, 0, 0, 0,
                "Todavía no hay historial de Más vendidos. Consulta al menos una categoría en \"Más vendidos MX\" para empezar.");
        }

        var demand = BuildDemandSignals(snapshots);
        var settings = await _pricingService.GetOrCreateSettingsAsync(organizationId, cancellationToken);

        var existing = await _context.PurchaseCandidates
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var existingById = existing.ToDictionary(x => x.MarketExternalId, StringComparer.OrdinalIgnoreCase);

        var top = demand
            .OrderByDescending(x => x.DemandScore)
            .ThenBy(x => x.BestPosition)
            .Take(MaxCandidates)
            .ToList();
        var topIds = top.Select(x => x.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Lo que ya salió del Top se marca para que deje de aparecer como sugerencia.
        foreach (var stale in existing.Where(x =>
                     !topIds.Contains(x.MarketExternalId) &&
                     PurchaseCandidateStatus.Reevaluable.Contains(x.Status)))
        {
            stale.DemandScore = 0;
            stale.Trend = PurchaseTrend.Exited;
            stale.SuggestedQuantity = 0;
            stale.UpdatedAt = DateTime.UtcNow;
        }

        int searched = 0, matched = 0, profitable = 0;
        string? warning = null;
        var ebayAvailable = true;

        foreach (var signal in top)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!existingById.TryGetValue(signal.ExternalId, out var candidate))
            {
                candidate = new PurchaseCandidate
                {
                    OrganizationId = organizationId,
                    MarketExternalId = signal.ExternalId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.PurchaseCandidates.Add(candidate);
                existingById[signal.ExternalId] = candidate;
            }

            ApplyDemand(candidate, signal);

            var reevaluable = PurchaseCandidateStatus.Reevaluable.Contains(candidate.Status);
            var needsSearch = reevaluable &&
                              ebayAvailable &&
                              searched < MaxSourceSearchesPerRefresh &&
                              (forceSourceSearch ||
                               candidate.LastSourceSearchAt is null ||
                               DateTime.UtcNow - candidate.LastSourceSearchAt > SourceSearchCacheTime);

            if (needsSearch)
            {
                try
                {
                    searched++;
                    if (await SearchSourceAsync(candidate, cancellationToken))
                        matched++;
                    await Task.Delay(DelayBetweenSearches, cancellationToken);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Ebay:", StringComparison.Ordinal))
                {
                    ebayAvailable = false;
                    warning = "Configura Ebay:ClientId y Ebay:ClientSecret para buscar automáticamente la fuente en eBay. " +
                              "Mientras tanto puedes vincular la oferta manualmente con su link.";
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "eBay search failed for candidate {ExternalId}.", candidate.MarketExternalId);
                    candidate.LastSourceSearchAt = DateTime.UtcNow;
                    warning ??= "Algunas búsquedas en eBay fallaron; se reintentarán en la próxima actualización.";
                }
            }

            if (reevaluable)
            {
                EvaluateProfitability(candidate, settings);
                if (candidate.Status == PurchaseCandidateStatus.Suggested)
                    profitable++;
            }

            candidate.LastEvaluatedAt = DateTime.UtcNow;
            candidate.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new PurchaseRadarRefreshResult(top.Count, searched, matched, profitable, warning);
    }

    public async Task<int> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-WindowDays);
        var organizationIds = await _context.MarketRankingSnapshots
            .AsNoTracking()
            .Where(x => x.SnapshotDate >= from)
            .Select(x => x.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var refreshed = 0;
        foreach (var organizationId in organizationIds)
        {
            try
            {
                var result = await RefreshAsync(organizationId, false, cancellationToken);
                _logger.LogInformation(
                    "Purchase radar refreshed for {OrganizationId}: {Evaluated} evaluated, {Matched} matched, {Profitable} profitable.",
                    organizationId, result.CandidatesEvaluated, result.SourcesMatched, result.Profitable);
                refreshed++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Purchase radar refresh failed for {OrganizationId}.", organizationId);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }

        return refreshed;
    }

    // ─────────────────────────────────────────────────────────────
    // Demanda
    // ─────────────────────────────────────────────────────────────

    private sealed record Appearance(
        DateOnly Date,
        int Position,
        MarketRankingSnapshot Snapshot,
        MarketRankingSnapshotItem Item);

    private sealed record DemandSignal(
        string ExternalId,
        string CategoryId,
        string CategoryName,
        string Name,
        string? Brand,
        string? Model,
        string? ImageUrl,
        string? Permalink,
        decimal? PriceMxn,
        int BestPosition,
        int? LatestPosition,
        int DaysInTop,
        int SnapshotDaysObserved,
        string Trend,
        int DemandScore);

    private static List<DemandSignal> BuildDemandSignals(IReadOnlyList<MarketRankingSnapshot> snapshots)
    {
        var categoryDates = snapshots
            .GroupBy(x => x.CategoryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.SnapshotDate).Distinct().OrderBy(d => d).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var appearances = snapshots
            .SelectMany(s => s.Items.Select(i => new Appearance(s.SnapshotDate, i.Position, s, i)))
            .Where(a => IsUsableItem(a.Item))
            .GroupBy(a => a.Item.ExternalId, StringComparer.OrdinalIgnoreCase);

        var signals = new List<DemandSignal>();

        foreach (var group in appearances)
        {
            // Si aparece en varias categorías, usa la categoría donde más se repite.
            var categoryGroup = group
                .GroupBy(a => a.Snapshot.CategoryId, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .First();

            var ordered = categoryGroup.OrderBy(a => a.Date).ToList();
            var latest = ordered[^1];
            var dates = categoryDates[categoryGroup.Key];
            var observedDays = dates.Count;
            var daysInTop = ordered.Select(a => a.Date).Distinct().Count();
            var latestCategoryDate = dates[^1];
            var bestPosition = ordered.Min(a => a.Position);
            var averagePosition = ordered.Average(a => a.Position);

            var trend = ResolveTrend(ordered, observedDays, latestCategoryDate);

            // Constancia (45): cuántos días de los observados estuvo en el Top, ponderado por cuántos días hay de historial.
            var consistency = observedDays == 0 ? 0d : (double)daysInTop / observedDays;
            var historyConfidence = Math.Min(observedDays, 7) / 7d;
            var consistencyScore = 45d * consistency * historyConfidence;

            // Posición (35): Top 1 vale más que Top 10.
            var positionScore = 35d * Math.Clamp((11d - averagePosition) / 10d, 0d, 1d);

            // Tendencia (20).
            var trendScore = trend switch
            {
                PurchaseTrend.Rising => 20d,
                PurchaseTrend.New => 15d,
                PurchaseTrend.Stable => 12d,
                PurchaseTrend.Falling => 4d,
                _ => 0d
            };

            var score = (int)Math.Round(Math.Clamp(consistencyScore + positionScore + trendScore, 0d, 100d));

            var latestPrice = ordered
                .Select(a => a.Item.Price)
                .LastOrDefault(p => p is > 0);

            signals.Add(new DemandSignal(
                latest.Item.ExternalId,
                latest.Snapshot.CategoryId,
                latest.Snapshot.CategoryName,
                latest.Item.Name,
                latest.Item.Brand,
                latest.Item.Model,
                latest.Item.ImageUrl,
                latest.Item.Permalink,
                latestPrice,
                bestPosition,
                latest.Date == latestCategoryDate ? latest.Position : null,
                daysInTop,
                observedDays,
                trend,
                score));
        }

        return signals;
    }

    private static string ResolveTrend(
        IReadOnlyList<Appearance> ordered,
        int observedDays,
        DateOnly latestCategoryDate)
    {
        var latest = ordered[^1];
        if (latest.Date != latestCategoryDate)
            return PurchaseTrend.Exited;

        if (observedDays >= 4 && ordered.Count <= 2)
            return PurchaseTrend.New;

        if (ordered.Count < 2)
            return PurchaseTrend.Stable;

        var half = Math.Max(1, ordered.Count / 2);
        var earlier = ordered.Take(half).Average(a => a.Position);
        var recent = ordered.Skip(ordered.Count - half).Average(a => a.Position);
        var delta = earlier - recent; // positivo = mejoró (número de posición menor)

        return delta switch
        {
            >= 1.5 => PurchaseTrend.Rising,
            <= -1.5 => PurchaseTrend.Falling,
            _ => PurchaseTrend.Stable
        };
    }

    private static bool IsUsableItem(MarketRankingSnapshotItem item) =>
        !string.IsNullOrWhiteSpace(item.ExternalId) &&
        !string.IsNullOrWhiteSpace(item.Name) &&
        item.Name.Length >= 6 &&
        !item.Name.StartsWith("Ficha no disponible", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(item.Name, item.ExternalId, StringComparison.OrdinalIgnoreCase);

    private static void ApplyDemand(PurchaseCandidate candidate, DemandSignal signal)
    {
        candidate.CategoryId = Truncate(signal.CategoryId, 100)!;
        candidate.CategoryName = Truncate(signal.CategoryName, 300)!;
        candidate.MarketName = Truncate(signal.Name, 500)!;
        candidate.MarketBrand = Truncate(signal.Brand, 200);
        candidate.MarketModel = Truncate(signal.Model, 200);
        candidate.MarketImageUrl = Truncate(signal.ImageUrl, 1000);
        candidate.MarketPermalink = Truncate(signal.Permalink, 1000);
        candidate.MarketPriceMxn = signal.PriceMxn ?? candidate.MarketPriceMxn;
        candidate.BestPosition = signal.BestPosition;
        candidate.LatestPosition = signal.LatestPosition;
        candidate.DaysInTop = signal.DaysInTop;
        candidate.SnapshotDaysObserved = signal.SnapshotDaysObserved;
        candidate.Trend = signal.Trend;
        candidate.DemandScore = signal.Trend == PurchaseTrend.Exited
            ? Math.Min(signal.DemandScore, 25)
            : signal.DemandScore;
    }

    // ─────────────────────────────────────────────────────────────
    // Fuente (eBay)
    // ─────────────────────────────────────────────────────────────

    private async Task<bool> SearchSourceAsync(PurchaseCandidate candidate, CancellationToken cancellationToken)
    {
        var query = await _queryBuilder.BuildAsync(
            candidate.MarketName,
            candidate.MarketBrand,
            candidate.MarketModel,
            cancellationToken);

        candidate.SourceSearchQuery = Truncate(query, 300);
        candidate.LastSourceSearchAt = DateTime.UtcNow;

        var hits = await _ebayApiService.SearchItemsAsync(query, 15, cancellationToken);
        var best = PickBestHit(candidate, query, hits);

        if (best is null)
        {
            ClearSource(candidate);
            return false;
        }

        ApplySource(candidate, best.Value.Hit, best.Value.Confidence);
        return true;
    }

    private static (EbaySearchHit Hit, int Confidence)? PickBestHit(
        PurchaseCandidate candidate,
        string query,
        IReadOnlyList<EbaySearchHit> hits)
    {
        if (hits.Count == 0)
            return null;

        var keyTokens = SourceSearchQueryBuilder.KeyTokens(
            candidate.MarketName, candidate.MarketBrand, candidate.MarketModel);
        var brand = SourceSearchQueryBuilder.Normalize(candidate.MarketBrand ?? string.Empty).Trim();
        var queryTokens = SourceSearchQueryBuilder.Tokenize(query)
            .Where(t => t.Length > 2)
            .Distinct()
            .ToList();

        var scored = hits
            .Select(hit => (Hit: hit, Confidence: ScoreMatch(hit, keyTokens, brand, queryTokens)))
            .Where(x => x.Confidence >= MinMatchConfidence)
            .ToList();

        if (scored.Count == 0)
            return null;

        // Entre las coincidencias casi tan buenas como la mejor, elige la más barata (producto + envío).
        var topConfidence = scored.Max(x => x.Confidence);
        return scored
            .Where(x => x.Confidence >= topConfidence - 15)
            .OrderBy(x => (x.Hit.PriceUsd ?? 0m) + (x.Hit.ShippingUsd ?? 0m))
            .ThenByDescending(x => x.Confidence)
            .First();
    }

    private static int ScoreMatch(
        EbaySearchHit hit,
        IReadOnlyList<string> keyTokens,
        string brand,
        IReadOnlyList<string> queryTokens)
    {
        var title = SourceSearchQueryBuilder.Normalize(hit.Title);
        var titleTokens = SourceSearchQueryBuilder.Tokenize(hit.Title).ToHashSet();
        var compactTitle = title.Replace("-", string.Empty).Replace(" ", string.Empty);

        double score = 0;

        if (keyTokens.Count > 0)
        {
            var keyHits = keyTokens.Count(k =>
                titleTokens.Contains(k) ||
                compactTitle.Contains(k.Replace("-", string.Empty), StringComparison.Ordinal));
            score += 55d * keyHits / keyTokens.Count;
        }

        if (!string.IsNullOrWhiteSpace(brand))
        {
            var hitBrand = SourceSearchQueryBuilder.Normalize(hit.Brand ?? string.Empty);
            if (title.Contains(brand, StringComparison.Ordinal) || hitBrand.Contains(brand, StringComparison.Ordinal))
                score += 20;
        }

        if (queryTokens.Count > 0)
        {
            var overlap = queryTokens.Count(t => titleTokens.Contains(t));
            var weight = keyTokens.Count > 0 ? 25d : 70d;
            score += weight * overlap / queryTokens.Count;
        }

        // Sin número de parte ni marca la coincidencia es menos confiable.
        if (keyTokens.Count == 0 && string.IsNullOrWhiteSpace(brand))
            score *= 0.85;

        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }

    private static void ApplySource(PurchaseCandidate candidate, EbaySearchHit hit, int confidence)
    {
        candidate.SourceMarketplace = "eBay";
        candidate.SourceProductId = Truncate(hit.LegacyItemId ?? hit.ItemId, 200);
        candidate.SourceUrl = Truncate(hit.ItemWebUrl, 1000);
        candidate.SourceTitle = Truncate(hit.Title, 500);
        candidate.SourceImageUrl = Truncate(hit.ImageUrl, 1000);
        candidate.SourceCondition = Truncate(hit.Condition, 100);
        candidate.SourcePriceUsd = hit.PriceUsd;
        candidate.SourceShippingUsd = hit.ShippingUsd;
        candidate.MatchConfidence = confidence;
    }

    private static void ClearSource(PurchaseCandidate candidate)
    {
        candidate.SourceMarketplace = null;
        candidate.SourceProductId = null;
        candidate.SourceUrl = null;
        candidate.SourceTitle = null;
        candidate.SourceImageUrl = null;
        candidate.SourceCondition = null;
        candidate.SourcePriceUsd = null;
        candidate.SourceShippingUsd = null;
        candidate.MatchConfidence = 0;
    }

    public async Task SetSourceManuallyAsync(
        Guid organizationId,
        long candidateId,
        string sourceUrlOrId,
        decimal? priceUsd,
        decimal? shippingUsd,
        CancellationToken cancellationToken = default)
    {
        sourceUrlOrId = sourceUrlOrId?.Trim() ?? string.Empty;
        if (sourceUrlOrId.Length == 0 || sourceUrlOrId.Length > 1000)
            throw new ArgumentException("Pega el link del producto en la tienda donde lo vas a comprar.");
        if (priceUsd is < 0 or > 100000 || shippingUsd is < 0 or > 10000)
            throw new ArgumentException("El precio o el envío no son válidos.");

        var candidate = await RequireCandidateAsync(organizationId, candidateId, cancellationToken);
        var isUrl = Uri.TryCreate(sourceUrlOrId, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        var isEbay = !isUrl || uri!.Host.Contains("ebay.", StringComparison.OrdinalIgnoreCase);

        if (isEbay && priceUsd is null)
        {
            // eBay tiene API oficial: se lee precio, título e imagen automáticamente.
            var item = await _ebayApiService.ExtractFromUrlOrSearchAsync(sourceUrlOrId);
            if (item.SourcePrice is not > 0)
                throw new InvalidOperationException("eBay no devolvió un precio para ese artículo.");

            ApplySource(candidate, new EbaySearchHit(
                item.SourceProductId,
                item.SourceProductId,
                item.Title ?? candidate.MarketName,
                item.SourcePrice,
                shippingUsd,
                item.SourceCurrency,
                item.ImageUrl,
                item.SourceUrl,
                item.Condition,
                item.Brand), 100);
        }
        else
        {
            // RockAuto, Amazon, tiendas en El Paso, etc.: sin API pública, el precio lo captura el usuario.
            if (!isUrl)
                throw new ArgumentException("Pega el link completo (https://...) del producto.");
            if (priceUsd is not > 0)
                throw new ArgumentException("Escribe el precio en dólares: esta tienda no tiene una API para leerlo automáticamente.");

            candidate.SourceMarketplace = SourceNameFromHost(uri!.Host);
            candidate.SourceProductId = null;
            candidate.SourceUrl = Truncate(uri.ToString(), 1000);
            candidate.SourceTitle = Truncate(candidate.MarketName, 500);
            candidate.SourceImageUrl = null;
            candidate.SourceCondition = "Nuevo";
            candidate.SourcePriceUsd = Math.Round(priceUsd.Value, 2);
            candidate.SourceShippingUsd = shippingUsd is null ? null : Math.Round(shippingUsd.Value, 2);
            candidate.MatchConfidence = 100;
        }

        candidate.SourceSearchQuery = "Vinculado manualmente";
        candidate.LastSourceSearchAt = DateTime.UtcNow;

        if (PurchaseCandidateStatus.Reevaluable.Contains(candidate.Status))
        {
            var settings = await _pricingService.GetOrCreateSettingsAsync(organizationId, cancellationToken);
            EvaluateProfitability(candidate, settings);
        }

        candidate.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string SourceNameFromHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        return host switch
        {
            _ when host.Contains("rockauto.") => "RockAuto",
            _ when host.Contains("amazon.") => "Amazon",
            _ when host.Contains("aliexpress.") => "AliExpress",
            _ when host.Contains("walmart.") => "Walmart",
            _ when host.Contains("autozone.") => "AutoZone",
            _ when host.Contains("oreillyauto.") => "O'Reilly",
            _ when host.Contains("advanceautoparts.") => "Advance Auto Parts",
            _ when host.Contains("summitracing.") => "Summit Racing",
            _ => host.Length > 50 ? host[..50] : host
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Rentabilidad
    // ─────────────────────────────────────────────────────────────

    private void EvaluateProfitability(PurchaseCandidate candidate, CrossBorderSettings settings)
    {
        if (candidate.SourcePriceUsd is not > 0)
        {
            candidate.Status = PurchaseCandidateStatus.NoSource;
            candidate.LandedCostMxn = null;
            candidate.EstimatedNetProfitMxn = null;
            candidate.EstimatedMarginPercent = null;
            candidate.RecommendedListingPriceMxn = null;
            candidate.SuggestedQuantity = 0;
            return;
        }

        var totalSourceUsd = candidate.SourcePriceUsd.Value + (candidate.SourceShippingUsd ?? 0m);
        var minimumPrice = _pricingService.CalculatePricing(totalSourceUsd, settings);
        candidate.LandedCostMxn = minimumPrice.LandedCostJuarezMxn;

        if (candidate.MarketPriceMxn is > 0)
        {
            var atMarket = _pricingService.CalculateProfitAtPrice(
                totalSourceUsd, candidate.MarketPriceMxn.Value, settings);

            candidate.EstimatedNetProfitMxn = atMarket.NetProfitMxn;
            candidate.EstimatedMarginPercent = atMarket.NetMarginPercent;

            if (atMarket.IsProfitable)
            {
                // Compite al precio del mercado: ya deja la ganancia mínima.
                candidate.RecommendedListingPriceMxn = candidate.MarketPriceMxn;
                candidate.Status = PurchaseCandidateStatus.Suggested;
                candidate.SuggestedQuantity = SuggestQuantity(candidate.DemandScore, candidate.Trend);
                return;
            }
        }
        else
        {
            candidate.EstimatedNetProfitMxn = null;
            candidate.EstimatedMarginPercent = null;
        }

        // Para ganar lo mínimo habría que vender más caro que el mercado.
        candidate.RecommendedListingPriceMxn = minimumPrice.SuggestedListingPriceMxn;
        candidate.Status = PurchaseCandidateStatus.LowMargin;
        candidate.SuggestedQuantity = 0;
    }

    private static int SuggestQuantity(int demandScore, string trend)
    {
        if (trend == PurchaseTrend.Exited)
            return 0;

        var quantity = demandScore switch
        {
            >= 80 => 5,
            >= 65 => 4,
            >= 50 => 3,
            _ => 2
        };

        return trend == PurchaseTrend.Falling ? Math.Max(1, quantity - 1) : quantity;
    }

    // ─────────────────────────────────────────────────────────────
    // Volver a comprar (ventas propias)
    // ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RestockSuggestion>> GetRestockSuggestionsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-30);

        var sales = await _context.SalesOrderItems
            .AsNoTracking()
            .Where(x => x.SalesOrder.OrganizationId == organizationId &&
                        x.SalesOrder.DateCreated >= since &&
                        x.SalesOrder.Status != "cancelled" &&
                        x.SalesOrder.Status != "invalid" &&
                        x.SellerSku != null && x.SellerSku != "")
            .GroupBy(x => x.SellerSku!)
            .Select(g => new
            {
                Sku = g.Key,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.UnitPrice * x.Quantity)
            })
            .ToListAsync(cancellationToken);

        if (sales.Count == 0)
            return Array.Empty<RestockSuggestion>();

        var skus = sales.Select(x => x.Sku).ToList();
        var variants = await _context.ProductVariants
            .AsNoTracking()
            .Include(x => x.Product)
            .Where(x => x.OrganizationId == organizationId && x.IsActive && skus.Contains(x.Sku))
            .ToListAsync(cancellationToken);

        var salesBySku = sales.ToDictionary(x => x.Sku, StringComparer.OrdinalIgnoreCase);
        var result = new List<RestockSuggestion>();

        foreach (var variant in variants)
        {
            if (!salesBySku.TryGetValue(variant.Sku, out var sold) || sold.Units <= 0)
                continue;

            var dailyRate = sold.Units / 30m;
            var stock = Math.Max(0, variant.Stock);
            var daysOfCover = dailyRate > 0 ? Math.Round(stock / dailyRate, 1) : (decimal?)null;
            var target = (int)Math.Ceiling(dailyRate * RestockTargetCoverDays);
            var suggested = Math.Max(0, target - stock);

            if (suggested <= 0 && stock > variant.LowStockThreshold)
                continue;

            result.Add(new RestockSuggestion(
                variant.Id,
                variant.ProductId,
                variant.Sku,
                variant.Product.Title ?? variant.Sku,
                variant.Product.ImageUrl,
                sold.Units,
                variant.Stock,
                daysOfCover,
                Math.Max(suggested, 1),
                variant.Product.SourceMarketplace,
                variant.Product.SourceUrl,
                variant.Product.SourcePrice,
                variant.Product.SourceCurrency,
                sold.Units > 0 ? Math.Round(sold.Revenue / sold.Units, 2) : null));
        }

        return result
            .OrderBy(x => x.DaysOfCover ?? decimal.MaxValue)
            .ThenByDescending(x => x.UnitsSold30Days)
            .Take(50)
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────
    // Compra y recepción
    // ─────────────────────────────────────────────────────────────

    public async Task<PurchaseConfirmation> MarkPurchasedAsync(
        Guid organizationId,
        long candidateId,
        int quantity,
        decimal? unitCostUsd,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        if (quantity is < 1 or > 500)
            throw new ArgumentException("La cantidad debe estar entre 1 y 500.");
        if (unitCostUsd is < 0 or > 100000)
            throw new ArgumentException("El costo unitario no es válido.");

        var candidate = await RequireCandidateAsync(organizationId, candidateId, cancellationToken);
        if (candidate.Status == PurchaseCandidateStatus.Purchased)
            throw new InvalidOperationException("Esta compra ya está registrada y pendiente de recibir.");

        var accountId = candidate.MarketplaceAccountId ?? await _context.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.Marketplace == "MercadoLibre" &&
                        x.Status == "Connected")
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var cost = unitCostUsd ?? candidate.SourcePriceUsd;

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        Product? product = null;
        if (candidate.ProductId.HasValue)
        {
            product = await _context.Products
                .Include(x => x.Variants)
                .Include(x => x.MarketplacePublications)
                .FirstOrDefaultAsync(x => x.Id == candidate.ProductId && x.OrganizationId == organizationId,
                    cancellationToken);
        }

        if (product is null)
        {
            product = new Product
            {
                OrganizationId = organizationId,
                CreatedByUserId = userId,
                SourceUrl = Truncate(candidate.SourceUrl ?? candidate.MarketPermalink ?? string.Empty, 1000)!,
                SourceMarketplace = candidate.SourceMarketplace ?? "Manual",
                SourceProductId = Truncate(candidate.SourceProductId, 200),
                // El título en español de Mercado Libre es mejor punto de partida para publicar en México.
                Title = Truncate(candidate.MarketName, 300),
                Description = "Producto comprado desde el Radar de compra. Revisa descripción, fotos propias, atributos y condición antes de publicar.",
                SourcePrice = cost,
                SourceCurrency = "USD",
                ImageUrl = Truncate(candidate.MarketImageUrl ?? candidate.SourceImageUrl, 1000),
                Brand = Truncate(candidate.MarketBrand, 200),
                Model = Truncate(candidate.MarketModel, 200),
                SourceStatus = "PurchaseRadar",
                LastSourceCheckAt = candidate.LastSourceSearchAt,
                Status = "NeedsReview",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Products.Add(product);
        }
        else
        {
            product.SourcePrice = cost ?? product.SourcePrice;
            product.UpdatedAt = DateTime.UtcNow;
        }

        if (!product.Variants.Any(x => x.IsActive))
        {
            product.Variants.Add(new ProductVariant
            {
                OrganizationId = organizationId,
                Sku = await GenerateUniqueSkuAsync(organizationId, candidate.Id, cancellationToken),
                AttributesJson = "{}",
                Price = candidate.RecommendedListingPriceMxn,
                Stock = 0, // se suma al confirmar la recepción
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var publication = product.MarketplacePublications.FirstOrDefault(x =>
            x.Marketplace == "MercadoLibre" && x.MarketplaceAccountId == accountId);
        if (publication is null)
        {
            publication = new MarketplacePublication
            {
                Product = product,
                Marketplace = "MercadoLibre",
                MarketplaceAccountId = accountId,
                Price = candidate.RecommendedListingPriceMxn ?? candidate.MarketPriceMxn,
                Stock = quantity,
                CurrencyId = "MXN",
                CategoryId = candidate.CategoryId,
                ListingTypeId = "gold_special",
                Condition = "new",
                Status = "Draft",
                IsPublished = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.MarketplacePublications.Add(publication);
        }
        else if (!publication.IsPublished)
        {
            publication.Price = candidate.RecommendedListingPriceMxn ?? publication.Price;
            publication.Stock = quantity;
            publication.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);

        candidate.ProductId = product.Id;
        candidate.MarketplaceAccountId = accountId;
        candidate.Status = PurchaseCandidateStatus.Purchased;
        candidate.PurchasedQuantity = quantity;
        candidate.PurchasedUnitCostUsd = cost;
        candidate.PurchasedAt = DateTime.UtcNow;
        candidate.ReceivedAt = null;
        candidate.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PurchaseConfirmation(candidate.Id, product.Id, accountId);
    }

    public async Task MarkReceivedAsync(
        Guid organizationId,
        long candidateId,
        int? receivedQuantity,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await RequireCandidateAsync(organizationId, candidateId, cancellationToken);
        if (candidate.Status != PurchaseCandidateStatus.Purchased || candidate.ProductId is null)
            throw new InvalidOperationException("Solo puedes recibir compras registradas como \"En camino\".");

        var quantity = receivedQuantity ?? candidate.PurchasedQuantity ?? 0;
        if (quantity is < 1 or > 500)
            throw new ArgumentException("La cantidad recibida debe estar entre 1 y 500.");

        var variant = await _context.ProductVariants
            .Where(x => x.OrganizationId == organizationId &&
                        x.ProductId == candidate.ProductId &&
                        x.IsActive)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("El producto ya no tiene un SKU activo para recibir el stock.");

        var referenceKey = $"purchase-candidate:{candidate.Id}:{candidate.PurchasedAt?.Ticks ?? 0}";
        var alreadyReceived = await _context.InventoryMovements
            .AnyAsync(x => x.OrganizationId == organizationId && x.ReferenceKey == referenceKey, cancellationToken);
        if (alreadyReceived)
            throw new InvalidOperationException("Esta compra ya se recibió.");

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        variant.Stock += quantity;
        variant.UpdatedAt = DateTime.UtcNow;

        _context.InventoryMovements.Add(new InventoryMovement
        {
            OrganizationId = organizationId,
            ProductVariantId = variant.Id,
            Type = "PurchaseReceipt",
            QuantityDelta = quantity,
            StockAfter = variant.Stock,
            ReferenceKey = referenceKey,
            Notes = Truncate($"Compra recibida desde el Radar ({candidate.SourceMarketplace ?? "fuente"}): {candidate.MarketName}", 500),
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        });

        candidate.Status = PurchaseCandidateStatus.Received;
        candidate.ReceivedAt = DateTime.UtcNow;
        candidate.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _stockSync.RequestPush(new[] { variant.ProductId });
    }

    // ─────────────────────────────────────────────────────────────
    // Utilidades
    // ─────────────────────────────────────────────────────────────

    private async Task<PurchaseCandidate> RequireCandidateAsync(
        Guid organizationId,
        long candidateId,
        CancellationToken cancellationToken) =>
        await _context.PurchaseCandidates
            .FirstOrDefaultAsync(x => x.Id == candidateId && x.OrganizationId == organizationId, cancellationToken)
        ?? throw new KeyNotFoundException("No se encontró el producto del radar.");

    private async Task<string> GenerateUniqueSkuAsync(
        Guid organizationId,
        long candidateId,
        CancellationToken cancellationToken)
    {
        var baseSku = $"RC-{candidateId}";
        var sku = baseSku;
        var suffix = 1;

        while (await _context.ProductVariants.AnyAsync(
                   x => x.OrganizationId == organizationId && x.Sku == sku, cancellationToken))
        {
            suffix++;
            sku = $"{baseSku}-{suffix}";
        }

        return sku;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
