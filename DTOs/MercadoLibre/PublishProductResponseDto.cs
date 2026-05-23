namespace MarketplaceSync.Web.DTOs.MercadoLibre
{
    public class PublishProductResponseDto
    {
        public bool Success { get; set; }

        public string? ItemId { get; set; }

        public string? Permalink { get; set; }

        public string? Status { get; set; }

        public string? Error { get; set; }
    }
}