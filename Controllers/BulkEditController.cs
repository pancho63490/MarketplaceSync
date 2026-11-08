using Hangfire;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Cambios masivos por Excel: bajar, subir, revisar y aplicar.</summary>
[Authorize]
public sealed class BulkEditController : Controller
{
    private readonly IOrganizationContext _organizationContext;
    private readonly BulkEditService _bulk;
    private readonly IBackgroundJobClient _jobs;

    public BulkEditController(IOrganizationContext organizationContext, BulkEditService bulk, IBackgroundJobClient jobs)
    {
        _organizationContext = organizationContext;
        _bulk = bulk;
        _jobs = jobs;
    }

    [HttpGet]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var bytes = await _bulk.ExportAsync(org, cancellationToken);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"publicaciones-{DateTime.UtcNow.AddHours(-7):yyyy-MM-dd}.xlsx");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (file is null || file.Length == 0)
        {
            TempData["Warning"] = "Escoge el archivo de Excel con tus cambios.";
            return RedirectToAction("Index", "Publications");
        }
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;
        var (import, error) = await _bulk.ParseAsync(org, stream, file.FileName, cancellationToken);
        if (import is null)
        {
            TempData["Error"] = error;
            return RedirectToAction("Index", "Publications");
        }
        return RedirectToAction(nameof(Review), new { id = import.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Review(int id, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var import = await _bulk.LoadAsync(org, id, cancellationToken);
        return import is null ? NotFound() : View(import);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(int id, List<int>? lines, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _bulk.StartAsync(org, id, lines ?? new List<int>(), cancellationToken);
        if (ok)
            _jobs.Enqueue<BulkEditService>(s => s.ApplyAsync(org, id, CancellationToken.None));
        TempData[ok ? "Success" : "Warning"] = message;
        return RedirectToAction(nameof(Review), new { id });
    }
}
