using System.Text.RegularExpressions;
using MarketplaceSync.Services.Invoicing;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Invoicing;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Facturas CFDI: configuración, lista del mes, factura global, cancelar y descargar.</summary>
[Authorize]
public sealed class InvoicesController : Controller
{
    private readonly IOrganizationContext _organizationContext;
    private readonly InvoicingService _invoicing;
    private readonly AppDbContext _context;

    public InvoicesController(IOrganizationContext organizationContext, InvoicingService invoicing, AppDbContext context)
    {
        _organizationContext = organizationContext;
        _invoicing = invoicing;
        _context = context;
    }

    public sealed class InvoicesIndexViewModel
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public List<SalesInvoice> Invoices { get; init; } = new();
        public required GlobalPreview Global { get; init; }
        public List<string> Missing { get; init; } = new();
        public bool Sandbox { get; init; }
        public int PendingRequests { get; init; }
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? year, int? month, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));
        var y = year ?? today.Year;
        var m = Math.Clamp(month ?? today.Month, 1, 12);
        if (new DateOnly(y, m, 1) > new DateOnly(today.Year, today.Month, 1))
            (y, m) = (today.Year, today.Month);

        var settings = await _invoicing.GetSettingsAsync(org, cancellationToken);
        var (from, to) = InvoicingService.MonthRange(y, m);
        var invoices = await _context.SalesInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == org && (i.IssuedAt >= from && i.IssuedAt < to || i.Kind == InvoiceKinds.Global && i.PeriodYear == y && i.PeriodMonth == m))
            .OrderByDescending(i => i.IssuedAt)
            .ToListAsync(cancellationToken);
        var since = DateTime.UtcNow.AddDays(-30);
        var pending = await _context.SalesOrders.CountAsync(o => o.OrganizationId == org && o.Status == "paid" &&
                                                                 o.BuyerRfc != null && o.SalesInvoiceId == null && o.DateCreated >= since, cancellationToken);

        return View(new InvoicesIndexViewModel
        {
            Year = y,
            Month = m,
            Invoices = invoices,
            Global = await _invoicing.GlobalPreviewAsync(org, y, m, cancellationToken),
            Missing = _invoicing.MissingSetup(settings),
            Sandbox = _invoicing.Provider.IsSandbox,
            PendingRequests = pending
        });
    }

    // ── Configuración ──

    [HttpGet]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _invoicing.GetSettingsAsync(org, cancellationToken);
        ViewBag.Configured = _invoicing.Provider.IsConfigured;
        ViewBag.Sandbox = _invoicing.Provider.IsSandbox;
        ViewBag.Missing = _invoicing.MissingSetup(settings);
        return View(settings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(
        string? issuerRfc, string? issuerName, string? fiscalRegime, string? expeditionZip, string? serie, string? globalSerie,
        int? nextFolio, string? defaultProductCode, decimal? ivaRate, string? paymentForm, bool uploadToMercadoLibre, bool notifyTelegram,
        CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var settings = await _invoicing.GetSettingsAsync(org, cancellationToken);
        var errors = new List<string>();

        var rfc = issuerRfc?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(rfc) && !SatCatalogs.LooksLikeRfc(rfc)) errors.Add("El RFC no tiene el formato correcto.");
        else if (rfc != settings.IssuerRfc)
        {
            settings.IssuerRfc = string.IsNullOrEmpty(rfc) ? null : rfc;
            settings.CsdUploadedAt = null; // otro RFC = otro CSD
            settings.CsdStatus = null;
        }
        settings.IssuerName = InvoicingService.CleanName(issuerName);
        settings.FiscalRegime = SatCatalogs.FiscalRegimes.Any(r => r.Code == fiscalRegime) ? fiscalRegime : null;
        if (!string.IsNullOrWhiteSpace(expeditionZip) && !Regex.IsMatch(expeditionZip.Trim(), @"^\d{5}$")) errors.Add("El código postal debe tener 5 dígitos.");
        else settings.ExpeditionZip = string.IsNullOrWhiteSpace(expeditionZip) ? null : expeditionZip.Trim();
        if (!string.IsNullOrWhiteSpace(defaultProductCode) && !Regex.IsMatch(defaultProductCode.Trim(), @"^\d{8}$")) errors.Add("La clave SAT debe tener 8 dígitos.");
        else settings.DefaultProductCode = string.IsNullOrWhiteSpace(defaultProductCode) ? null : defaultProductCode.Trim();
        settings.Serie = CleanSerie(serie, "ML");
        settings.GlobalSerie = CleanSerie(globalSerie, "GL");
        // Solo hacia adelante: un formulario viejo no debe regresar el folio y repetir números.
        if (nextFolio is { } folio && folio > settings.NextFolio) settings.NextFolio = folio;
        settings.IvaRate = ivaRate is 0.08m ? 0.08m : 0.16m;
        settings.PaymentForm = SatCatalogs.PaymentForms.Any(p => p.Code == paymentForm) ? paymentForm! : "31";
        settings.UploadToMercadoLibre = uploadToMercadoLibre;
        settings.NotifyTelegram = notifyTelegram;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        TempData[errors.Count == 0 ? "Success" : "Warning"] = errors.Count == 0 ? "Datos fiscales guardados." : string.Join(" ", errors);
        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(200_000)]
    public async Task<IActionResult> UploadCsd(IFormFile? certificate, IFormFile? privateKey, string? password, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (certificate is null || privateKey is null || string.IsNullOrEmpty(password))
        {
            TempData["Warning"] = "Sube el archivo .cer, el .key y escribe la contraseña de la llave privada.";
            return RedirectToAction(nameof(Settings));
        }
        if (!certificate.FileName.EndsWith(".cer", StringComparison.OrdinalIgnoreCase) || !privateKey.FileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Warning"] = "El certificado debe ser .cer y la llave .key (los del Certificado de Sello Digital, no los de la e.firma).";
            return RedirectToAction(nameof(Settings));
        }
        // Se leen en memoria y van directo a Facturama: la app no los guarda.
        var (ok, message) = await _invoicing.UploadCsdAsync(org, await ReadAsync(certificate, cancellationToken), await ReadAsync(privateKey, cancellationToken), password, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Settings));
    }

    // ── Factura individual ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(int orderId, ReceiverInput receiver, Dictionary<int, string>? satCodes, bool rememberCodes, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message, _) = await _invoicing.IssueIndividualAsync(org, orderId, receiver, satCodes, rememberCodes, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction("Details", "Orders", new { id = orderId }, "factura");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(int id, int? orderId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _invoicing.UploadToMercadoLibreAsync(org, id, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return orderId is { } o ? RedirectToAction("Details", "Orders", new { id = o }, "factura") : RedirectToAction(nameof(Index));
    }

    // ── Factura global ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueGlobal(int year, int month, bool includeRfc, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message, _) = await _invoicing.IssueGlobalAsync(org, year, month, includeRfc, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Index), new { year, month });
    }

    // ── Cancelar y descargar ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? motive, int? orderId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _invoicing.CancelAsync(org, id, motive ?? "02", cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return orderId is { } o ? RedirectToAction("Details", "Orders", new { id = o }, "factura") : RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Download(int id, string? format, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (bytes, fileName, contentType) = await _invoicing.DownloadAsync(org, id, format ?? "pdf", cancellationToken);
        if (bytes is null)
        {
            TempData["Error"] = "No pude descargar la factura de Facturama. Intenta en un momento.";
            return RedirectToAction(nameof(Index));
        }
        return File(bytes, contentType, fileName);
    }

    private static string CleanSerie(string? value, string fallback)
    {
        var clean = Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToUpperInvariant();
        return clean.Length is > 0 and <= 10 ? clean : fallback;
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        return stream.ToArray();
    }
}
