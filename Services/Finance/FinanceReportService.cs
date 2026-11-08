using System.Globalization;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Helpers;
using MarketplaceSync.Web.Models.Finance;
using Microsoft.EntityFrameworkCore;
using C = MarketplaceSync.Web.Helpers.SimpleXlsxWriter.Cell;
using S = MarketplaceSync.Web.Helpers.SimpleXlsxWriter.CellStyle;

namespace MarketplaceSync.Services.Finance;

public sealed record SaleLine(
    DateTime Date, string OrderId, string? Buyer, string Products, int Units,
    decimal Charged, decimal Fees, decimal Shipping, bool ShippingIsReal, decimal Tax, decimal? Cost, decimal? Profit);

public sealed record ExpenseLine(int Id, DateOnly Date, string Category, string Description, decimal Amount, string Currency, decimal AmountMxn, bool IsMonthly, string Source);

public sealed record PurchaseLine(DateTime Date, string Product, int Quantity, decimal? UnitUsd, decimal? TotalUsd, decimal? TotalMxn, string Origin);

/// <summary>El mes del negocio: ventas con su ganancia, gastos, compras y la ganancia neta.</summary>
public sealed record MonthReport(
    int Year, int Month,
    IReadOnlyList<SaleLine> Sales, IReadOnlyList<ExpenseLine> Expenses, IReadOnlyList<PurchaseLine> Purchases,
    decimal UsdToMxn, decimal WithholdingPercent)
{
    public decimal Charged => Sales.Sum(s => s.Charged);
    public decimal Fees => Sales.Sum(s => s.Fees);
    public decimal Shipping => Sales.Sum(s => s.Shipping);
    public decimal Tax => Sales.Sum(s => s.Tax);
    public decimal CostOfGoods => Sales.Sum(s => s.Cost ?? 0);
    public decimal SalesProfit => Sales.Sum(s => s.Profit ?? 0);
    public int SalesWithoutCost => Sales.Count(s => s.Profit is null);
    public decimal ExpensesTotal => Expenses.Sum(e => e.AmountMxn);
    /// <summary>Gasto en Mercado Ads del mes (null si no hay Ads o el mes pasa de los 90 días que guarda Mercado Libre).</summary>
    public decimal? AdsSpend { get; init; }
    public decimal NetProfit => SalesProfit - ExpensesTotal - (AdsSpend ?? 0);
    public decimal PurchasesUsd => Purchases.Sum(p => p.TotalUsd ?? 0);
    public string Title => CultureInfo.GetCultureInfo("es-MX").TextInfo.ToTitleCase(new DateTime(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.GetCultureInfo("es-MX")));
}

/// <summary>Reporte mensual (pantalla de Gastos, Telegram y el Excel para el contador) y alta de gastos.</summary>
public sealed class FinanceReportService
{
    private readonly AppDbContext _context;
    private readonly ICrossBorderPricingService _pricing;
    private readonly OrderProfitService _profits;

    private readonly IServiceProvider _services;

    public FinanceReportService(AppDbContext context, ICrossBorderPricingService pricing, OrderProfitService profits, IServiceProvider services)
    {
        _context = context;
        _pricing = pricing;
        _profits = profits;
        _services = services;
    }

    public static DateOnly TodayJuarez => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));

    public async Task<BusinessExpense> AddExpenseAsync(
        Guid org, DateOnly date, string category, string description, decimal amount, string currency, bool monthly, string source,
        CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        currency = currency?.ToUpperInvariant() == "USD" ? "USD" : "MXN";
        var expense = new BusinessExpense
        {
            OrganizationId = org,
            Date = date,
            Category = ExpenseCategories.All.Any(c => c.Id == category) ? category : ExpenseCategories.Guess(description),
            Description = (description ?? string.Empty).Trim() is { Length: > 0 } d ? (d.Length > 200 ? d[..200] : d) : ExpenseCategories.Label(category),
            Amount = Math.Round(Math.Abs(amount), 2),
            Currency = currency,
            AmountMxn = Math.Round(Math.Abs(amount) * (currency == "USD" ? settings.UsdToMxnRate : 1m), 2),
            IsMonthly = monthly,
            Source = source
        };
        _context.BusinessExpenses.Add(expense);
        await _context.SaveChangesAsync(cancellationToken);
        return expense;
    }

    public async Task<MonthReport> MonthAsync(Guid org, int year, int month, CancellationToken cancellationToken = default)
    {
        var first = new DateOnly(year, month, 1);
        var next = first.AddMonths(1);
        DateTime Utc(DateOnly d) => DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue).AddHours(7), DateTimeKind.Utc);
        var from = Utc(first);
        var to = Utc(next);
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);

        // ── Ventas ──
        var orders = await _context.SalesOrders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= from && o.DateCreated < to)
            .OrderBy(o => o.DateCreated)
            .ToListAsync(cancellationToken);
        var missing = orders.Where(o => o.ProfitComputedAt is null).ToList();
        if (missing.Count > 0)
        {
            // Ventas viejas que no se calcularon: con envío estimado, sin guardar.
            var landed = await _profits.LandedCostsAsync(org, missing.SelectMany(o => o.Items).ToList(), settings, cancellationToken);
            foreach (var order in missing)
                OrderProfitService.Compute(order, landed, settings, null);
        }
        var sales = orders.Select(o => new SaleLine(
            o.DateCreated.AddHours(-7), o.ExternalOrderId, o.BuyerNickname,
            string.Join(" + ", o.Items.Select(i => (i.Quantity > 1 ? $"{i.Quantity}× " : "") + (i.Title ?? i.ExternalItemId))),
            o.Items.Sum(i => i.Quantity), o.TotalAmount, o.Items.Sum(i => (i.SaleFee ?? 0) * i.Quantity),
            o.SellerShippingCostMxn ?? 0, o.ShippingCostIsReal == true, o.TaxWithheldMxn ?? 0, o.ProductCostMxn, o.RealProfitMxn)).ToList();

        // ── Gastos (los mensuales cuentan cada mes desde que empezaron) ──
        var expenses = await _context.BusinessExpenses.AsNoTracking()
            .Where(e => e.OrganizationId == org &&
                        ((!e.IsMonthly && e.Date >= first && e.Date < next) ||
                         (e.IsMonthly && e.Date < next && (e.EndedOn == null || e.EndedOn >= first))))
            .OrderBy(e => e.Date)
            .ToListAsync(cancellationToken);
        var expenseLines = expenses.Select(e => new ExpenseLine(
            e.Id, e.IsMonthly ? new DateOnly(year, month, Math.Min(e.Date.Day, DateTime.DaysInMonth(year, month))) : e.Date,
            e.Category, e.Description, e.Amount, e.Currency, e.AmountMxn, e.IsMonthly, e.Source)).ToList();

        // ── Compras en EE.UU. ──
        var candidates = await _context.PurchaseCandidates.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.PurchasedAt >= from && c.PurchasedAt < to)
            .Select(c => new { c.PurchasedAt, Title = c.SourceTitle ?? c.MarketName, c.PurchasedQuantity, c.PurchasedUnitCostUsd, c.SourcePriceUsd })
            .ToListAsync(cancellationToken);
        var purchases = candidates.Select(c =>
        {
            var unit = c.PurchasedUnitCostUsd ?? c.SourcePriceUsd;
            var qty = c.PurchasedQuantity ?? 0;
            return new PurchaseLine(c.PurchasedAt!.Value.AddHours(-7), c.Title ?? "(sin título)", qty, unit,
                unit * qty, unit * qty * settings.UsdToMxnRate, "Producto nuevo");
        }).ToList();
        var restock = await _context.PurchaseListItems.AsNoTracking()
            .Where(i => i.Kind == "restock" && i.PurchaseList.OrganizationId == org &&
                        i.PurchaseList.ApprovedAt >= from && i.PurchaseList.ApprovedAt < to)
            .Select(i => new { i.PurchaseList.ApprovedAt, i.Title, i.Quantity, i.UnitCostUsd })
            .ToListAsync(cancellationToken);
        purchases.AddRange(restock.Select(i => new PurchaseLine(i.ApprovedAt!.Value.AddHours(-7), i.Title, i.Quantity, i.UnitCostUsd,
            i.UnitCostUsd * i.Quantity, i.UnitCostUsd * i.Quantity * settings.UsdToMxnRate, "Resurtido")));

        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var adsSpend = await _services.GetRequiredService<MarketplaceSync.Services.MercadoLibre.AdsService>()
            .SpendAsync(org, new DateOnly(year, month, 1), lastDay, cancellationToken);
        return new MonthReport(year, month, sales, expenseLines, purchases.OrderBy(p => p.Date).ToList(),
            settings.UsdToMxnRate, settings.PlatformTaxWithholdingPercent)
        {
            AdsSpend = adsSpend is > 0 ? adsSpend : null
        };
    }

    /// <summary>Excel para el contador: Resumen, Ventas, Gastos y Compras del mes.</summary>
    public static byte[] BuildWorkbook(MonthReport report, string businessName)
    {
        var xlsx = new SimpleXlsxWriter();

        var summary = xlsx.AddSheet("Resumen", 44, 18, 60);
        summary.Add(new() { new C($"{businessName} — {report.Title}", S.Bold) });
        summary.Add(new() { new C("Concepto", S.Header), new C("Monto", S.Header), new C("Nota", S.Header) });
        void Line(string label, decimal value, string note = "", bool bold = false) =>
            summary.Add(new() { new C(label, bold ? S.Bold : S.Text), new C(value, bold ? S.BoldMoney : S.Money), new C(note) });
        Line("Ventas cobradas", report.Charged, $"{report.Sales.Count} ventas pagadas en Mercado Libre");
        Line("Comisiones de Mercado Libre", -report.Fees, "sale_fee de cada venta");
        Line("Envíos pagados por el vendedor", -report.Shipping, "según Mercado Libre; estimado donde no respondió");
        Line("Retenciones ISR + IVA", -report.Tax, $"calculadas al {report.WithholdingPercent:0.##}% del precio; el comprobante oficial (CFDI de retenciones) está en tu cuenta de Mercado Libre");
        Line("Costo de lo vendido", -report.CostOfGoods, report.SalesWithoutCost > 0 ? $"{report.SalesWithoutCost} ventas sin costo capturado (no se restan)" : "costo puesto en Juárez");
        Line("Ganancia de las ventas", report.SalesProfit, "", true);
        Line("Gastos del negocio", -report.ExpensesTotal, $"{report.Expenses.Count} gastos (ver hoja Gastos)");
        if (report.AdsSpend is { } ads)
            Line("Publicidad en Mercado Libre (Mercado Ads)", -ads, "según Mercado Ads; Mercado Libre la factura aparte");
        Line("Ganancia neta del mes", report.NetProfit, "", true);
        summary.Add(new());
        summary.Add(new() { new C("Compras en EE.UU. (US$)", S.Bold), new C(report.PurchasesUsd, S.Money), new C($"≈ {(report.PurchasesUsd * report.UsdToMxn).ToString("C0", CultureInfo.GetCultureInfo("es-MX"))} a {report.UsdToMxn:0.00}; no se restan de la ganancia (ya están en el costo de lo vendido)") });
        summary.Add(new());
        summary.Add(new() { new C("Generado por MarketplaceSync. Las retenciones y el costo son cálculos de la app: confírmalos con los comprobantes de Mercado Libre y tus facturas.") });

        var sales = xlsx.AddSheet("Ventas", 12, 16, 18, 60, 8, 14, 14, 14, 14, 14, 14);
        sales.Add(new() { H("Fecha"), H("Pedido"), H("Comprador"), H("Productos"), H("Piezas"), H("Cobrado"), H("Comisión"), H("Envío"), H("Retención"), H("Costo"), H("Ganancia") });
        foreach (var s in report.Sales)
            sales.Add(new() { new C(s.Date), new C(s.OrderId), new C(s.Buyer), new C(s.Products), new C(s.Units, S.Integer), new C(s.Charged, S.Money), new C(s.Fees, S.Money),
                new C(s.Shipping, S.Money), new C(s.Tax, S.Money), new C(s.Cost, S.Money), new C(s.Profit, S.Money) });
        sales.Add(new() { new C("Total", S.Bold), new C(null), new C(null), new C(null), new C(report.Sales.Sum(s => s.Units), S.Integer), new C(report.Charged, S.BoldMoney),
            new C(report.Fees, S.BoldMoney), new C(report.Shipping, S.BoldMoney), new C(report.Tax, S.BoldMoney), new C(report.CostOfGoods, S.BoldMoney), new C(report.SalesProfit, S.BoldMoney) });

        var expenses = xlsx.AddSheet("Gastos", 12, 24, 50, 14, 8, 14, 10);
        expenses.Add(new() { H("Fecha"), H("Categoría"), H("Descripción"), H("Monto"), H("Moneda"), H("Monto MXN"), H("Mensual") });
        foreach (var e in report.Expenses)
            expenses.Add(new() { new C(e.Date), new C(ExpenseCategories.Label(e.Category)), new C(e.Description), new C(e.Amount, S.Money), new C(e.Currency),
                new C(e.AmountMxn, S.Money), new C(e.IsMonthly ? "Sí" : "") });
        expenses.Add(new() { new C("Total", S.Bold), new C(null), new C(null), new C(null), new C(null), new C(report.ExpensesTotal, S.BoldMoney) });

        var purchases = xlsx.AddSheet("Compras", 12, 60, 8, 12, 14, 14, 16);
        purchases.Add(new() { H("Fecha"), H("Producto"), H("Piezas"), H("US$ c/u"), H("Total US$"), H("≈ MXN"), H("Tipo") });
        foreach (var p in report.Purchases)
            purchases.Add(new() { new C(p.Date), new C(p.Product), new C(p.Quantity, S.Integer), new C(p.UnitUsd, S.Money), new C(p.TotalUsd, S.Money), new C(p.TotalMxn, S.Money), new C(p.Origin) });

        return xlsx.Build();

        static C H(string text) => new(text, S.Header);
    }
}
