namespace MarketplaceSync.Web.ViewModels
{
    public class ExtractedProductInfo
    {
        public string? SourceUrl { get; set; }

        public string? SourceMarketplace { get; set; }

        public string? SourceProductId { get; set; }

        public string? Title { get; set; }

        public string? Description { get; set; }

        public decimal? SourcePrice { get; set; }

        public string? SourceCurrency { get; set; }

        public int? SourceStock { get; set; }

        public string? ImageUrl { get; set; }

        public string? SourceStatus { get; set; }

        public string? Brand { get; set; }

        public string? Model { get; set; }

        public string? Condition { get; set; }
    }
}