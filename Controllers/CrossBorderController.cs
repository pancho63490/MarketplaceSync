using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Models.CrossBorder;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketplaceSync.Web.Controllers;

[Authorize]
public sealed class CrossBorderController : Controller
{
    private readonly ICrossBorderPricingService _pricingService;
    private readonly IOrganizationContext _organizationContext;
    private readonly ILogger<CrossBorderController> _logger;

    public CrossBorderController(
        ICrossBorderPricingService pricingService,
        IOrganizationContext organizationContext,
        ILogger<CrossBorderController> logger)
    {
        _pricingService = pricingService;
        _organizationContext = organizationContext;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);

        var settings = await _pricingService
            .GetOrCreateSettingsAsync(organizationId, cancellationToken);

        var sampleCalculation = _pricingService.CalculatePricing(35.00m, settings);

        ViewBag.SampleCalculation = sampleCalculation;
        return View(settings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        CrossBorderSettings input,
        CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);

        ModelState.Remove(nameof(input.Organization));
        ModelState.Remove(nameof(input.OrganizationId));

        if (input.UsdToMxnRate <= 0)
            ModelState.AddModelError(nameof(input.UsdToMxnRate), "El tipo de cambio debe ser mayor a 0.");

        if (input.ImportFeePercent < 0 || input.ImportFeePercent > 100)
            ModelState.AddModelError(nameof(input.ImportFeePercent), "El porcentaje de cruce debe estar entre 0% y 100%.");

        if (input.TargetProfitMarginPercent < 0 || input.TargetProfitMarginPercent > 300)
            ModelState.AddModelError(nameof(input.TargetProfitMarginPercent), "El margen debe estar entre 0% y 300%.");

        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();
            _logger.LogWarning("Validation failed saving CrossBorderSettings: {Errors}", string.Join("; ", errors));
            TempData["Error"] = "Verifica los datos ingresados: " + string.Join(", ", errors);
            ViewBag.SampleCalculation = _pricingService.CalculatePricing(35.00m, input);
            return View(nameof(Index), input);
        }

        try
        {
            var existing = await _pricingService
                .GetOrCreateSettingsAsync(organizationId, cancellationToken);

            existing.UsdToMxnRate = input.UsdToMxnRate;
            existing.ImportFeePercent = input.ImportFeePercent;
            existing.AdditionalHandlingFeeUsd = input.AdditionalHandlingFeeUsd;
            existing.TargetProfitMarginPercent = input.TargetProfitMarginPercent;
            existing.MinProfitAmountMxn = input.MinProfitAmountMxn;
            existing.EstimatedShippingMxn = input.EstimatedShippingMxn;
            existing.DefaultMarketplaceFeePercent = input.DefaultMarketplaceFeePercent;
            existing.PlatformTaxWithholdingPercent = input.PlatformTaxWithholdingPercent;
            existing.AutoPublishEnabled = input.AutoPublishEnabled;

            await _pricingService.SaveSettingsAsync(existing, cancellationToken);

            TempData["Success"] = "Reglas de costeo e impuestos El Paso ➔ Juárez actualizadas correctamente.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Error al guardar reglas de costeo fronterizo.");
            TempData["Error"] = "No fue posible guardar la configuración. Intenta nuevamente.";
            ViewBag.SampleCalculation = _pricingService.CalculatePricing(35.00m, input);
            return View(nameof(Index), input);
        }
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Simulate(
        [FromForm] decimal sourcePriceUsd,
        [FromForm] decimal? customFeePercent,
        CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext
            .RequireCurrentOrganizationIdAsync(cancellationToken);

        var settings = await _pricingService
            .GetOrCreateSettingsAsync(organizationId, cancellationToken);

        var breakdown = _pricingService.CalculatePricing(sourcePriceUsd, settings, customFeePercent);
        return Json(breakdown);
    }
}
