namespace MarketplaceSync.Services.Interfaces
{
    public interface IMercadoLibreService
    {
        Task<bool> PublishProductAsync(int productId, Guid organizationId);

        Task<string?> PredictCategoryAsync(string title, Guid organizationId);

        Task<string> GetAccessTokenAsync(Guid organizationId);
        Task<string?> GetMeAsync(Guid organizationId);

Task<string?> GetCategoryAttributesAsync(string categoryId, Guid organizationId);

Task<string?> GetNicknameAsync(string accessToken);
    }
}
