namespace MarketplaceSync.Web.DTOs.MercadoLibre
{
    public class PublishProductRequestDto
    {
        public string Title { get; set; } = string.Empty;

        public decimal Price { get; set; }

        public int AvailableQuantity { get; set; }

        public string CategoryId { get; set; } = string.Empty;

        public string CurrencyId { get; set; } = "MXN";

        public string Condition { get; set; } = "new";

        public string ListingTypeId { get; set; } = "gold_special";
    }
}