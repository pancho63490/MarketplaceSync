using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.MarketInsights;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MarketInsights;

public sealed class MarketRankingHistoryService : IMarketRankingHistoryService
{
    private readonly AppDbContext _dbContext;
    private readonly IMercadoLibreAuthService _authService;
    private readonly IMercadoLibreBestSellerService _bestSellerService;
    private readonly IMarketOpportunityAnalyzer _opportunityAnalyzer;
    private readonly ILogger<MarketRankingHistoryService> _logger;

    public MarketRankingHistoryService(
        AppDbContext dbContext,
        IMercadoLibreAuthService authService,
        IMercadoLibreBestSellerService bestSellerService,
        IMarketOpportunityAnalyzer opportunityAnalyzer,
        ILogger<MarketRankingHistoryService> logger)
    {
        _dbContext = dbContext;
        _authService = authService;
        _bestSellerService = bestSellerService;
        _opportunityAnalyzer = opportunityAnalyzer;
        _logger = logger;
    }

    public async Task<bool> CaptureAsync(
        Guid organizationId,
        int marketplaceAccountId,
        MercadoLibreBestSellerResult ranking,
        MarketOpportunityAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var alreadyCaptured = await _dbContext.MarketRankingSnapshots
            .AsNoTracking()
            .AnyAsync(x => x.OrganizationId == organizationId &&
                           x.CategoryId == ranking.CategoryId &&
                           x.SnapshotDate == snapshotDate,
                cancellationToken);
        if (alreadyCaptured)
            return false;

        var scores = analysis.Opportunities.ToDictionary(
            x => x.Item.Id,
            x => x.Score,
            StringComparer.OrdinalIgnoreCase);
        var snapshot = new MarketRankingSnapshot
        {
            OrganizationId = organizationId,
            MarketplaceAccountId = marketplaceAccountId,
            CategoryId = Truncate(ranking.CategoryId, 100),
            CategoryName = Truncate(ranking.CategoryName, 300),
            TotalActiveListings = ranking.TotalActiveListings,
            SnapshotDate = snapshotDate,
            CapturedAt = DateTime.UtcNow,
            Items = ranking.Items.Select(item => new MarketRankingSnapshotItem
            {
                Position = item.Position,
                ExternalId = Truncate(item.Id, 200),
                Type = Truncate(item.Type, 30),
                Name = Truncate(item.Name, 500),
                Brand = TruncateNullable(item.Brand, 200),
                Model = TruncateNullable(item.Model, 200),
                ImageUrl = TruncateNullable(item.ImageUrl, 1000),
                Price = item.Price,
                CurrencyId = TruncateNullable(item.CurrencyId, 20),
                Permalink = TruncateNullable(item.Permalink, 1000),
                OpportunityScore = scores.GetValueOrDefault(item.Id)
            }).ToList()
        };

        _dbContext.MarketRankingSnapshots.Add(snapshot);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (!cancellationToken.IsCancellationRequested)
        {
            foreach (var entry in _dbContext.ChangeTracker.Entries()
                         .Where(x => x.State == EntityState.Added)
                         .ToArray())
                entry.State = EntityState.Detached;
            if (await _dbContext.MarketRankingSnapshots
                    .AsNoTracking()
                    .AnyAsync(x => x.OrganizationId == organizationId &&
                                   x.CategoryId == ranking.CategoryId &&
                                   x.SnapshotDate == snapshotDate,
                        cancellationToken))
            {
                _logger.LogInformation(
                    "Ranking snapshot was captured concurrently for organization {OrganizationId} and category {CategoryId}.",
                    organizationId,
                    ranking.CategoryId);
                return false;
            }

            _logger.LogError(
                exception,
                "Could not save ranking snapshot for organization {OrganizationId} and category {CategoryId}.",
                organizationId,
                ranking.CategoryId);
            throw;
        }
    }

    public async Task<int> CaptureAllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await _dbContext.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.Marketplace == "MercadoLibre" &&
                        x.Status == "Connected" &&
                        x.Tokens.Any(token => token.IsActive))
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.OrganizationId })
            .ToListAsync(cancellationToken);
        var firstAccountByOrganization = accounts
            .GroupBy(x => x.OrganizationId)
            .Select(x => x.First())
            .ToArray();
        var captured = 0;

        foreach (var account in firstAccountByOrganization)
        {
            var categories = await _dbContext.MarketplacePublications
                .AsNoTracking()
                .Where(x => x.Product.OrganizationId == account.OrganizationId &&
                            x.Marketplace == "MercadoLibre" &&
                            x.CategoryId != null &&
                            x.CategoryId.StartsWith("MLM"))
                .GroupBy(x => x.CategoryId!)
                .OrderByDescending(x => x.Count())
                .Select(x => x.Key)
                .Take(12)
                .ToListAsync(cancellationToken);

            if (categories.Count == 0)
                continue;

            string accessToken;
            try
            {
                accessToken = await _authService.GetValidAccessTokenAsync(
                    account.OrganizationId,
                    account.Id);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Could not get Mercado Libre token for ranking history account {MarketplaceAccountId}.",
                    account.Id);
                continue;
            }

            var existingIds = await GetExistingProductIdsAsync(
                account.OrganizationId,
                cancellationToken);
            foreach (var categoryId in categories)
            {
                try
                {
                    var ranking = await _bestSellerService.GetTopAsync(
                        categoryId,
                        accessToken,
                        10,
                        cancellationToken);
                    var analysis = _opportunityAnalyzer.Analyze(ranking, existingIds);
                    if (await CaptureAsync(
                            account.OrganizationId,
                            account.Id,
                            ranking,
                            analysis,
                            cancellationToken))
                        captured++;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not capture ranking history for organization {OrganizationId}, category {CategoryId}.",
                        account.OrganizationId,
                        categoryId);
                }
            }
        }

        return captured;
    }

    public async Task<MarketRankingHistory> GetHistoryAsync(
        Guid organizationId,
        string categoryId,
        int days,
        CancellationToken cancellationToken = default)
    {
        days = days is 7 or 30 or 90 ? days : 30;
        var firstDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-(days - 1)));
        var snapshots = await _dbContext.MarketRankingSnapshots
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.CategoryId == categoryId &&
                        x.SnapshotDate >= firstDate)
            .Include(x => x.Items)
            .OrderBy(x => x.SnapshotDate)
            .ThenBy(x => x.CapturedAt)
            .ToListAsync(cancellationToken);
        if (snapshots.Count == 0)
            return new MarketRankingHistory(days, 0, null, null, [], []);

        var latest = snapshots[^1];
        var previous = snapshots.Count > 1 ? snapshots[^2] : null;
        var currentItems = latest.Items.ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);
        var previousItems = previous?.Items.ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, MarketRankingSnapshotItem>(StringComparer.OrdinalIgnoreCase);
        var trends = currentItems.Keys
            .Union(previousItems.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(id => BuildTrend(id, currentItems, previousItems))
            .OrderBy(x => x.HasExited)
            .ThenBy(x => x.CurrentPosition ?? int.MaxValue)
            .ThenBy(x => x.PreviousPosition ?? int.MaxValue)
            .ToArray();
        var series = latest.Items
            .OrderBy(x => x.Position)
            .Take(5)
            .Select(item => new MarketRankingSeries(
                item.ExternalId,
                item.Name,
                snapshots.Select(snapshot => new MarketRankingSeriesPoint(
                    snapshot.SnapshotDate,
                    snapshot.Items.FirstOrDefault(x => x.ExternalId == item.ExternalId)?.Position))
                    .ToArray()))
            .ToArray();

        return new MarketRankingHistory(
            days,
            snapshots.Count,
            latest.CapturedAt,
            previous?.CapturedAt,
            trends,
            series);
    }

    private async Task<HashSet<string>> GetExistingProductIdsAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var publicationIds = await _dbContext.MarketplacePublications
            .AsNoTracking()
            .Where(x => x.Product.OrganizationId == organizationId &&
                        x.Marketplace == "MercadoLibre" &&
                        x.ExternalItemId != null)
            .Select(x => x.ExternalItemId!)
            .ToListAsync(cancellationToken);
        var opportunityIds = await _dbContext.Products
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.SourceMarketplace == "MercadoLibreInsights" &&
                        x.SourceProductId != null)
            .Select(x => x.SourceProductId!)
            .ToListAsync(cancellationToken);
        var result = publicationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.UnionWith(opportunityIds);
        return result;
    }

    private static MarketRankingTrendItem BuildTrend(
        string externalId,
        IReadOnlyDictionary<string, MarketRankingSnapshotItem> current,
        IReadOnlyDictionary<string, MarketRankingSnapshotItem> previous)
    {
        current.TryGetValue(externalId, out var currentItem);
        previous.TryGetValue(externalId, out var previousItem);
        return new MarketRankingTrendItem(
            externalId,
            currentItem?.Name ?? previousItem?.Name ?? externalId,
            currentItem?.ImageUrl ?? previousItem?.ImageUrl,
            currentItem?.Position,
            previousItem?.Position,
            currentItem is not null && previousItem is not null
                ? previousItem.Position - currentItem.Position
                : null,
            currentItem?.Price,
            previousItem?.Price,
            currentItem?.OpportunityScore,
            currentItem is not null && previousItem is null,
            currentItem is null && previousItem is not null);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateNullable(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= maxLength ? value : value[..maxLength];
}
