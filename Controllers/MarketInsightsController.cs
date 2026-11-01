using System.Text.RegularExpressions;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
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
    private readonly ILogger<MarketInsightsController> _logger;

    public MarketInsightsController(
        AppDbContext dbContext,
        IOrganizationContext organizationContext,
        IMercadoLibreAuthService authService,
        IMercadoLibreBestSellerService bestSellerService,
        ILogger<MarketInsightsController> logger)
    {
        _dbContext = dbContext;
        _organizationContext = organizationContext;
        _authService = authService;
        _bestSellerService = bestSellerService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? categoryId,
        int? marketplaceAccountId,
        CancellationToken cancellationToken)
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
        var savedCategories = savedCategoryCounts
            .Select(x => new SavedMarketplaceCategory(x.CategoryId, x.ProductCount))
            .ToList();

        var automaticallySelected = string.IsNullOrWhiteSpace(categoryId) && savedCategories.Count > 0;
        categoryId = automaticallySelected
            ? savedCategories[0].CategoryId
            : categoryId?.Trim().ToUpperInvariant();
        if (marketplaceAccountId.HasValue &&
            accounts.All(x => x.Value != marketplaceAccountId.Value.ToString()))
            marketplaceAccountId = null;
        marketplaceAccountId ??= accounts.Count > 0 ? int.Parse(accounts[0].Value!) : null;

        MercadoLibreBestSellerResult? ranking = null;
        string? errorMessage = null;
        if (accounts.Count == 0)
        {
            errorMessage = "Conecta una cuenta de Mercado Libre para consultar el ranking oficial.";
        }
        else if (string.IsNullOrWhiteSpace(categoryId))
        {
            errorMessage = "Agrega una categoría MLM para consultar los productos más vendidos.";
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

        return View(new MarketInsightsViewModel
        {
            CategoryId = categoryId,
            MarketplaceAccountId = marketplaceAccountId,
            CategorySelectedAutomatically = automaticallySelected,
            Accounts = accounts,
            SavedCategories = savedCategories,
            Ranking = ranking,
            ErrorMessage = errorMessage
        });
    }
}
