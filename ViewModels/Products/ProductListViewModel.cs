using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.ViewModels.Products;

/// <summary>Estado visible del producto, resumido para la lista (en español).</summary>
public enum ProductStage
{
    Draft,
    NeedsReview,
    Ready,
    Published,
    Paused,
    OutOfStock,
    Archived
}

public sealed class ProductListRow
{
    public required Product Product { get; init; }
    public MarketplacePublication? Publication { get; init; }
    public ProductStage Stage { get; init; }
    public int OwnStock { get; init; }
    public int SkuCount { get; init; }

    public static ProductStage ResolveStage(Product product, MarketplacePublication? publication)
    {
        if (string.Equals(product.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            return ProductStage.Archived;

        if (publication?.IsPublished == true)
        {
            return string.Equals(publication.Status, "paused", StringComparison.OrdinalIgnoreCase)
                ? ProductStage.Paused
                : ProductStage.Published;
        }

        return product.Status switch
        {
            var s when string.Equals(s, "OutOfStock", StringComparison.OrdinalIgnoreCase) => ProductStage.OutOfStock,
            var s when string.Equals(s, "ReadyToPublish", StringComparison.OrdinalIgnoreCase) => ProductStage.Ready,
            var s when string.Equals(s, "NeedsReview", StringComparison.OrdinalIgnoreCase) => ProductStage.NeedsReview,
            _ => ProductStage.Draft
        };
    }
}

public sealed class ProductListViewModel
{
    public IReadOnlyList<ProductListRow> Rows { get; init; } = Array.Empty<ProductListRow>();
    public IReadOnlyDictionary<ProductStage, int> Counts { get; init; } = new Dictionary<ProductStage, int>();
    public int TotalCount { get; init; }
    /// <summary>Productos sin publicar cuyo título sigue en inglés.</summary>
    public int EnglishTitleCount { get; init; }
    public string? Search { get; init; }
    public ProductStage? Stage { get; init; }

    public static (string Label, string Css, string Help) Describe(ProductStage stage) => stage switch
    {
        ProductStage.Draft => ("Borrador", "stage-draft", "Faltan datos para publicar"),
        ProductStage.NeedsReview => ("Por revisar", "stage-review", "Revisa título, fotos y atributos"),
        ProductStage.Ready => ("Listo para publicar", "stage-ready", "Ya puedes publicarlo"),
        ProductStage.Published => ("Publicado", "stage-published", "Activo en Mercado Libre"),
        ProductStage.Paused => ("Pausado", "stage-paused", "Pausado en Mercado Libre"),
        ProductStage.OutOfStock => ("Sin stock en la fuente", "stage-out", "El proveedor se quedó sin piezas"),
        ProductStage.Archived => ("Archivado", "stage-draft", ""),
        _ => (stage.ToString(), "stage-draft", "")
    };
}
