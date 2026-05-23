namespace MarketplaceSync.Services.Interfaces
{
    public interface IMercadoLibreService
    {
        Task<bool> PublishProductAsync(int productId);

        Task<string?> PredictCategoryAsync(string title);

        Task<string> GetAccessTokenAsync();
        Task<string?> GetMeAsync();

Task<string?> GetCategoryAttributesAsync(string categoryId);

Task<string?> GetNicknameAsync(string accessToken);
    }
}