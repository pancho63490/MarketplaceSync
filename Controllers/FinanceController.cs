using System.Globalization;
using MarketplaceSync.Services.Finance;
using MarketplaceSync.Web.Services.Tenancy;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Gastos del negocio, ganancia neta del mes y el Excel para el contador.</summary>
[Authorize]
public sealed class FinanceController : Controller
{
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly FinanceReportService _finance;

    public FinanceController(AppDbContext context, IOrganizationContext organizationContext, FinanceReportService finance)
    {
        _context = context;
        _organizationContext = organizationContext;
        _finance = finance;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? year, int? month, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (y, m) = Month(year, month);
        var report = await _finance.MonthAsync(org, y, m, cancellationToken);
        return View(report);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(DateOnly? date, string? category, string? description, string? amount, string? currency, bool monthly, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (!decimal.TryParse((amount ?? "").Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            TempData["Error"] = "Escribe el monto del gasto (por ejemplo 250 o 35.50).";
            return RedirectToAction(nameof(Index));
        }
        var day = date ?? FinanceReportService.TodayJuarez;
        var expense = await _finance.AddExpenseAsync(org, day, string.IsNullOrWhiteSpace(category) ? ExpenseCategories.Guess(description) : category, description ?? string.Empty, value, currency ?? "MXN", monthly, "web", cancellationToken);
        TempData["Success"] = $"Anotado: {expense.Description} · {expense.AmountMxn.ToString("C2", CultureInfo.GetCultureInfo("es-MX"))}" + (monthly ? " cada mes." : ".");
        return RedirectToAction(nameof(Index), new { year = day.Year, month = day.Month });
    }

    /// <summary>Borra un gasto; uno mensual deja de repetirse desde este mes (los meses pasados se quedan).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(int id, int year, int month, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var expense = await _context.BusinessExpenses.FirstOrDefaultAsync(e => e.Id == id && e.OrganizationId == org, cancellationToken);
        if (expense is null)
            return NotFound();

        var firstOfMonth = new DateOnly(year, month, 1);
        if (expense.IsMonthly && expense.Date < firstOfMonth)
        {
            expense.EndedOn = firstOfMonth.AddDays(-1);
            TempData["Success"] = $"«{expense.Description}» ya no se cuenta desde este mes.";
        }
        else
        {
            _context.BusinessExpenses.Remove(expense);
            TempData["Success"] = $"Quitado: {expense.Description}.";
        }
        await _context.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index), new { year, month });
    }

    [HttpGet]
    public async Task<IActionResult> Export(int? year, int? month, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (y, m) = Month(year, month);
        var report = await _finance.MonthAsync(org, y, m, cancellationToken);
        var name = await _context.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.Name).FirstOrDefaultAsync(cancellationToken) ?? "Mi negocio";
        var bytes = FinanceReportService.BuildWorkbook(report, name);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"reporte-{y}-{m:00}.xlsx");
    }

    private static (int Year, int Month) Month(int? year, int? month)
    {
        var today = FinanceReportService.TodayJuarez;
        var y = year is >= 2020 and <= 2100 ? year.Value : today.Year;
        var m = month is >= 1 and <= 12 ? month.Value : today.Month;
        return (y, m);
    }
}
