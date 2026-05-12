namespace MarketplaceSync.Web.ViewModels
{
    public class MercadoLibreAttributeInput
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool Required { get; set; }

        public string? ValueName { get; set; }

        public string? ValueType { get; set; }

        public List<MercadoLibreAttributeValueOption> Values { get; set; } = new();
    }

    public class MercadoLibreAttributeValueOption
    {
        public string? Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}