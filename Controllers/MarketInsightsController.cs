using System.Text.RegularExpressions;
using System.Security.Claims;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Services.Tenancy;
using MarketplaceSync.Web.ViewModels.MarketInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

[Authorize]
public sealed class MarketInsightsController : Controller
{
    private static readonly Regex MexicoCategoryPattern =
        new("^MLM\\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppDbContext _dbContext;
    private readonly IOrganizationContext _organizationContext;
    private readonly IMercadoLibreAuthService _authService;
    private readonly IMercadoLibreBestSellerService _bestSellerService;
    private readonly IMarketOpportunityAnalyzer _opportunityAnalyzer;
    private readonly IMarketRankingHistoryService _rankingHistoryService;
    private readonly MarketplaceSync.Web.Services.MercadoLibreCategoryService _categoryService;
    private readonly ILogger<MarketInsightsController> _logger;

    public MarketInsightsController(
        AppDbContext dbContext,
        IOrganizationContext organizationContext,
        IMercadoLibreAuthService authService,
        IMercadoLibreBestSellerService bestSellerService,
        IMarketOpportunityAnalyzer opportunityAnalyzer,
        IMarketRankingHistoryService rankingHistoryService,
        MarketplaceSync.Web.Services.MercadoLibreCategoryService categoryService,
        ILogger<MarketInsightsController> logger)
    {
        _dbContext = dbContext;
        _organizationContext = organizationContext;
        _authService = authService;
        _bestSellerService = bestSellerService;
        _opportunityAnalyzer = opportunityAnalyzer;
        _rankingHistoryService = rankingHistoryService;
        _categoryService = categoryService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? categoryId,
        int? marketplaceAccountId,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        var accounts = await _dbContext.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.Marketplace == "MercadoLibre" &&
                        x.Status == "Connected" &&
                        x.Tokens.Any(token => token.IsActive))
            .OrderBy(x => x.DisplayName)
            .Select(x => new SelectListItem
            {
                Value = x.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(x.DisplayName) ? x.ExternalAccountId : x.DisplayName
            })
            .ToListAsync(cancellationToken);
        var savedCategoryCounts = await _dbContext.MarketplacePublications
            .AsNoTracking()
            .Where(x => x.Product.OrganizationId == organizationId &&
                        x.Marketplace == "MercadoLibre" &&
                        x.CategoryId != null &&
                        x.CategoryId.StartsWith("MLM"))
            .GroupBy(x => x.CategoryId!)
            .Select(group => new
            {
                CategoryId = group.Key,
                ProductCount = group.Count()
            })
            .OrderByDescending(x => x.ProductCount)
            .ThenBy(x => x.CategoryId)
            .Take(12)
            .ToListAsync(cancellationToken);
        // Nombres legibles de las categorías ya consultadas (el historial guarda el nombre).
        var categoryNames = await _dbContext.MarketRankingSnapshots
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .GroupBy(x => x.CategoryId)
            .Select(g => new { CategoryId = g.Key, Name = g.OrderByDescending(x => x.CapturedAt).Select(x => x.CategoryName).First() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var savedCategories = savedCategoryCounts
            .Select(x => new SavedMarketplaceCategory(x.CategoryId, x.ProductCount, categoryNames.GetValueOrDefault(x.CategoryId)))
            .ToList();

        // Categorías consultadas antes aunque no tengan productos propios.
        foreach (var (id, name) in categoryNames)
        {
            if (savedCategories.Count >= 16)
                break;
            if (savedCategories.All(x => !string.Equals(x.CategoryId, id, StringComparison.OrdinalIgnoreCase)))
                savedCategories.Add(new SavedMarketplaceCategory(id, 0, name));
        }

        // El campo acepta un ID MLM o texto libre ("bomba de agua"): el texto se convierte en categoría.
        string? searchText = null;
        var categorySuggestions = new List<CategorySearchSuggestion>();
        var typed = categoryId?.Trim();
        if (!string.IsNullOrWhiteSpace(typed) && !MexicoCategoryPattern.IsMatch(typed))
        {
            searchText = typed.Length > 120 ? typed[..120] : typed;
            try
            {
                var suggestions = await _categoryService.SuggestCategoriesAsync(searchText);
                categorySuggestions = suggestions
                    .Where(x => !string.IsNullOrWhiteSpace(x.Category_Id) && MexicoCategoryPattern.IsMatch(x.Category_Id!))
                    .DistinctBy(x => x.Category_Id)
                    .Select(x => new CategorySearchSuggestion(
                        x.Category_Id!,
                        x.Category_Name ?? x.Domain_Name ?? x.Category_Id!))
                    .Take(6)
                    .ToList();
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Category search failed for '{Text}'.", searchText);
            }

            categoryId = categorySuggestions.FirstOrDefault()?.CategoryId;
        }

        var automaticallySelected = string.IsNullOrWhiteSpace(categoryId) && savedCategories.Count > 0;
        categoryId = automaticallySelected
            ? savedCategories[0].CategoryId
            : categoryId?.Trim().ToUpperInvariant();
        if (marketplaceAccountId.HasValue &&
            accounts.All(x => x.Value != marketplaceAccountId.Value.ToString()))
            marketplaceAccountId = null;
        marketplaceAccountId ??= accounts.Count > 0 ? int.Parse(accounts[0].Value!) : null;

        MercadoLibreBestSellerResult? ranking = null;
        MarketOpportunityAnalysis? opportunityAnalysis = null;
        MarketRankingHistory? history = null;
        string? errorMessage = null;
        if (accounts.Count == 0)
        {
            errorMessage = "Conecta una cuenta de Mercado Libre para consultar el ranking oficial.";
        }
        else if (string.IsNullOrWhiteSpace(categoryId))
        {
            errorMessage = searchText is null
                ? null
                : $"No encontramos una categoría de Mercado Libre para \"{searchText}\". Prueba con otras palabras, por ejemplo el nombre de la pieza.";
        }
        else if (!MexicoCategoryPattern.IsMatch(categoryId))
        {
            errorMessage = "La categoría debe ser de México y tener formato MLM seguido de números.";
        }
        else
        {
            try
            {
                var accessToken = await _authService.GetValidAccessTokenAsync(
                    organizationId,
                    marketplaceAccountId!.Value);
                ranking = await _bestSellerService.GetTopAsync(
                    categoryId,
                    accessToken,
                    10,
                    cancellationToken);
                var existingItemIdList = await _dbContext.MarketplacePublications
                    .AsNoTracking()
                    .Where(x => x.Product.OrganizationId == organizationId &&
                                x.Marketplace == "MercadoLibre" &&
                                x.ExternalItemId != null)
                    .Select(x => x.ExternalItemId!)
                    .ToListAsync(cancellationToken);
                var importedOpportunityIds = await _dbContext.Products
                    .AsNoTracking()
                    .Where(x => x.OrganizationId == organizationId &&
                                x.SourceMarketplace == "MercadoLibreInsights" &&
                                x.SourceProductId != null)
                    .Select(x => x.SourceProductId!)
                    .ToListAsync(cancellationToken);
                var existingItemIds = existingItemIdList.ToHashSet(StringComparer.OrdinalIgnoreCase);
                existingItemIds.UnionWith(importedOpportunityIds);
                opportunityAnalysis = _opportunityAnalyzer.Analyze(ranking, existingItemIds);
                try
                {
                    await _rankingHistoryService.CaptureAsync(
                        organizationId,
                        marketplaceAccountId.Value,
                        ranking,
                        opportunityAnalysis,
                        cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not save ranking history for category {CategoryId}.",
                        categoryId);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Could not retrieve Mercado Libre best sellers for category {CategoryId}.",
                    categoryId);
                errorMessage = "No fue posible obtener el ranking. Verifica que sea una categoría final de Mercado Libre y vuelve a intentar.";
            }
        }

        if (!string.IsNullOrWhiteSpace(categoryId) && MexicoCategoryPattern.IsMatch(categoryId))
        {
            try
            {
                history = await _rankingHistoryService.GetHistoryAsync(
                    organizationId,
                    categoryId,
                    days,
                    cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Could not load ranking history for category {CategoryId}.",
                    categoryId);
            }
        }

        return View(new MarketInsightsViewModel
        {
            CategoryId = categoryId,
            MarketplaceAccountId = marketplaceAccountId,
            CategorySelectedAutomatically = automaticallySelected,
            SearchText = searchText,
            CategorySuggestions = categorySuggestions,
            Accounts = accounts,
            SavedCategories = savedCategories,
            Ranking = ranking,
            OpportunityAnalysis = opportunityAnalysis,
            History = history,
            ErrorMessage = errorMessage
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PrepareForPublishing(
        string categoryId,
        int marketplaceAccountId,
        string itemId,
        CancellationToken cancellationToken)
    {
        categoryId = categoryId?.Trim().ToUpperInvariant() ?? string.Empty;
        itemId = itemId?.Trim() ?? string.Empty;
        if (categoryId.Length > 100 ||
            itemId.Length > 200 ||
            !MexicoCategoryPattern.IsMatch(categoryId) ||
            string.IsNullOrWhiteSpace(itemId))
        {
            TempData["Error"] = "La oportunidad seleccionada no contiene una categoría o producto válido.";
            return RedirectToAction(nameof(Index), new { categoryId, marketplaceAccountId });
        }

        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);
        var accountExists = await _dbContext.MarketplaceAccounts
            .AsNoTracking()
            .AnyAsync(x => x.Id == marketplaceAccountId &&
                           x.OrganizationId == organizationId &&
                           x.Marketplace == "MercadoLibre" &&
                           x.Status == "Connected" &&
                           x.Tokens.Any(token => token.IsActive),
                cancellationToken);
        if (!accountExists)
        {
            TempData["Error"] = "La cuenta de Mercado Libre ya no está conectada.";
            return RedirectToAction(nameof(Index), new { categoryId });
        }

        try
        {
            var accessToken = await _authService.GetValidAccessTokenAsync(
                organizationId,
                marketplaceAccountId);
            var ranking = await _bestSellerService.GetTopAsync(
                categoryId,
                accessToken,
                10,
                cancellationToken);
            var rankedItem = ranking.Items.FirstOrDefault(x =>
                string.Equals(x.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (rankedItem is null)
            {
                TempData["Error"] = "El producto ya no forma parte del Top 10. Actualiza el ranking e inténtalo nuevamente.";
                return RedirectToAction(nameof(Index), new { categoryId, marketplaceAccountId });
            }
            if (!rankedItem.DetailsAvailable)
            {
                TempData["Error"] = "Mercado Libre solo compartió el ID y la posición de este User Product, no su ficha. Elige un resultado que muestre nombre e imagen reales.";
                return RedirectToAction(nameof(Index), new { categoryId, marketplaceAccountId });
            }

            var product = await _dbContext.Products
                .Include(x => x.MarketplacePublications)
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId &&
                                          x.SourceMarketplace == "MercadoLibreInsights" &&
                                          x.SourceProductId == rankedItem.Id,
                    cancellationToken);
            var created = product is null;
            if (product is null)
            {
                product = new Product
                {
                    OrganizationId = organizationId,
                    CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
                    SourceUrl = Truncate(BuildSourceUrl(rankedItem), 1000)!,
                    SourceMarketplace = "MercadoLibreInsights",
                    SourceProductId = rankedItem.Id,
                    Title = Truncate(rankedItem.Name, 300),
                    Description = "Borrador creado desde el detector de oportunidades de Mercado Libre México. Verifica descripción, imágenes, proveedor, propiedad intelectual, precio, stock y atributos antes de publicar.",
                    SourcePrice = rankedItem.Price,
                    SourceCurrency = rankedItem.CurrencyId ?? "MXN",
                    ImageUrl = Truncate(rankedItem.ImageUrl, 1000),
                    Brand = Truncate(rankedItem.Brand, 200),
                    Model = Truncate(rankedItem.Model, 200),
                    SourceStatus = "MarketOpportunity",
                    Status = "NeedsReview",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _dbContext.Products.Add(product);
            }

            var publication = product.MarketplacePublications.FirstOrDefault(x =>
                x.Marketplace == "MercadoLibre" &&
                x.MarketplaceAccountId == marketplaceAccountId);
            if (publication is null)
            {
                publication = new MarketplacePublication
                {
                    Product = product,
                    Marketplace = "MercadoLibre",
                    MarketplaceAccountId = marketplaceAccountId,
                    Price = rankedItem.Price,
                    Stock = 1,
                    CurrencyId = rankedItem.CurrencyId ?? "MXN",
                    CategoryId = categoryId,
                    ListingTypeId = "gold_special",
                    Condition = "new",
                    Status = "Draft",
                    IsPublished = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _dbContext.MarketplacePublications.Add(publication);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            TempData["Success"] = created
                ? "Borrador creado. Revisa los datos, completa los atributos y confirma stock y precio antes de publicar."
                : "Este producto ya estaba en tu catálogo. Abrimos su borrador para continuar.";

            return RedirectToAction(
                "PublishToMercadoLibre",
                "Products",
                new { id = product.Id, marketplaceAccountId });
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Could not prepare market opportunity {ItemId} for publishing.",
                itemId);
            TempData["Error"] = "No fue posible preparar este producto. Actualiza el ranking y vuelve a intentar.";
            return RedirectToAction(nameof(Index), new { categoryId, marketplaceAccountId });
        }
    }

    private static string BuildSourceUrl(MercadoLibreBestSellerItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Permalink))
            return item.Permalink;

        var resource = item.Type.ToUpperInvariant() switch
        {
            "PRODUCT" => "products",
            "USER_PRODUCT" => "user-products",
            _ => "items"
        };
        return $"https://api.mercadolibre.com/{resource}/{Uri.EscapeDataString(item.Id)}";
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
}
