namespace MarketplaceSync.Services.Interfaces;

public sealed record CategoryAttributeSchema(
    string Id,
    string Name,
    bool Required,
    string? ValueType,
    IReadOnlyList<CategoryAttributeAllowedValue> AllowedValues);

public sealed record CategoryAttributeAllowedValue(
    string Id,
    string Name);

public sealed record ProductAiEnrichmentRequest(
    string RawTitle,
    string? RawDescription,
    string? RawBrand,
    string? RawModel,
    decimal? SourcePriceUsd,
    string? SourceUrl,
    string? SourceMarketplace,
    string CategoryId,
    string? CategoryName,
    IReadOnlyList<CategoryAttributeSchema> RequiredAttributes);

public sealed record ProductAiEnrichmentResult(
    string OptimizedTitle,
    string CleanDescription,
    string? Brand,
    string? Model,
    IReadOnlyDictionary<string, string> Attributes,
    bool HighRiskIntellectualProperty,
    string? IntellectualPropertyWarning,
    int ConfidenceScore,
    string ProcessingEngine);

public interface IProductAiEnricherService
{
    Task<ProductAiEnrichmentResult> EnrichProductAsync(
        ProductAiEnrichmentRequest request,
        CancellationToken cancellationToken = default);
}
