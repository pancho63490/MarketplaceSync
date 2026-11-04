using MarketplaceSync.Services.Interfaces;

namespace MarketplaceSync.Services.MarketInsights;

public sealed class MarketOpportunityAnalyzer : IMarketOpportunityAnalyzer
{
    public MarketOpportunityAnalysis Analyze(
        MercadoLibreBestSellerResult ranking,
        IReadOnlySet<string> existingItemIds)
    {
        var prices = ranking.Items
            .Where(x => x.Price is > 0)
            .Select(x => x.Price!.Value)
            .OrderBy(x => x)
            .ToArray();
        var medianPrice = CalculateMedian(prices);
        var categoryCompetitionScore = GetCategoryCompetitionScore(ranking.TotalActiveListings);
        var knownBrands = ranking.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.Brand))
            .GroupBy(x => x.Brand!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        var opportunities = ranking.Items
            .Select(item => AnalyzeItem(
                item,
                medianPrice,
                categoryCompetitionScore,
                knownBrands,
                ranking.Items.Count,
                existingItemIds))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Position)
            .ToArray();

        return new MarketOpportunityAnalysis(
            medianPrice,
            ranking.TotalActiveListings,
            GetCategoryCompetitionLevel(ranking.TotalActiveListings),
            opportunities);
    }

    private static MarketOpportunity AnalyzeItem(
        MercadoLibreBestSellerItem item,
        decimal? medianPrice,
        int categoryCompetitionScore,
        IReadOnlyDictionary<string, int> knownBrands,
        int rankedItemCount,
        IReadOnlySet<string> existingItemIds)
    {
        var demandScore = Math.Clamp(105 - (item.Position * 5), 35, 100);
        var priceScore = GetPriceScore(item.Price, medianPrice);
        var brandScore = GetBrandDifferentiationScore(item.Brand, knownBrands, rankedItemCount);
        var competitionScore = (int)Math.Round((categoryCompetitionScore * .6) + (brandScore * .4));
        var score = (int)Math.Round((demandScore * .50) + (priceScore * .25) + (competitionScore * .25));
        score = Math.Clamp(score, 0, 100);
        if (!item.DetailsAvailable)
            score = Math.Min(score, 39);
        var level = !item.DetailsAvailable ? "Limitada" : score switch
        {
            >= 80 => "Alta",
            >= 65 => "Buena",
            >= 50 => "Media",
            _ => "Baja"
        };

        return new MarketOpportunity(
            item,
            score,
            demandScore,
            priceScore,
            competitionScore,
            level,
            item.DetailsAvailable
                ? BuildRecommendation(score, item.Price, medianPrice)
                : "Mercado Libre no comparte la ficha de este producto de otro vendedor.",
            existingItemIds.Contains(item.Id));
    }

    private static int GetPriceScore(decimal? price, decimal? medianPrice)
    {
        if (price is not > 0 || medianPrice is not > 0)
            return 50;

        var ratio = price.Value / medianPrice.Value;
        return Math.Clamp((int)Math.Round(50 + ((1 - ratio) * 100)), 0, 100);
    }

    private static int GetBrandDifferentiationScore(
        string? brand,
        IReadOnlyDictionary<string, int> brandCounts,
        int rankedItemCount)
    {
        if (string.IsNullOrWhiteSpace(brand) || rankedItemCount == 0)
            return 50;

        var count = brandCounts.GetValueOrDefault(brand, 1);
        var share = (decimal)count / rankedItemCount;
        return Math.Clamp((int)Math.Round(100 - (share * 100)), 20, 95);
    }

    private static int GetCategoryCompetitionScore(int? totalActiveListings) => totalActiveListings switch
    {
        null => 50,
        <= 500 => 95,
        <= 2_000 => 80,
        <= 10_000 => 65,
        <= 50_000 => 45,
        _ => 25
    };

    private static string GetCategoryCompetitionLevel(int? totalActiveListings) => totalActiveListings switch
    {
        null => "Sin datos",
        <= 500 => "Baja",
        <= 2_000 => "Moderada",
        <= 10_000 => "Media",
        <= 50_000 => "Alta",
        _ => "Muy alta"
    };

    private static string BuildRecommendation(int score, decimal? price, decimal? medianPrice)
    {
        if (score >= 80)
            return "Prioridad alta: valida proveedor, margen y disponibilidad antes de incorporarlo.";
        if (price is > 0 && medianPrice is > 0 && price < medianPrice)
            return "Precio competitivo dentro del Top 10; revisa costos y margen potencial.";
        if (score >= 65)
            return "Buena señal de demanda; compara proveedor, garantía y diferenciadores.";

        return "Demanda visible, pero conviene validar margen y nivel de competencia.";
    }

    private static decimal? CalculateMedian(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
            return null;

        var middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }
}
