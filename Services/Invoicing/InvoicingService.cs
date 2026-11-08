using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Invoicing;
using MarketplaceSync.Web.Models.Orders;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Invoicing;

/// <summary>Datos fiscales que el comprador capturó en Mercado Libre.</summary>
public sealed record BuyerFiscalData(string? Rfc, string? Name, string? FiscalRegime, string? CfdiUse, string? Zip, string? PersonType)
{
    public bool HasRfc => SatCatalogs.LooksLikeRfc(Rfc) && Rfc != SatCatalogs.PublicRfc && Rfc != SatCatalogs.ForeignRfc;
}

/// <summary>Lo que el vendedor confirma antes de timbrar.</summary>
public sealed class ReceiverInput
{
    public string? Rfc { get; set; }
    public string? Name { get; set; }
    public string? FiscalRegime { get; set; }
    public string? CfdiUse { get; set; }
    public string? Zip { get; set; }
}

public sealed class InvoiceLine
{
    public int? ProductId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Sku { get; init; }
    public int Quantity { get; init; }

    /// <summary>Precio por pieza con IVA incluido (lo que pagó el comprador).</summary>
    public decimal UnitPrice { get; init; }

    public string? SatCode { get; set; }
    public decimal Total => UnitPrice * Quantity;
}

/// <summary>Una venta (o carrito) lista para facturar.</summary>
public sealed class InvoiceDraft
{
    public required SalesOrder Order { get; init; }
    public List<SalesOrder> Orders { get; init; } = new();
    public List<InvoiceLine> Lines { get; init; } = new();
    public decimal Total => Lines.Sum(l => l.Total);
    public BuyerFiscalData? Buyer { get; init; }
    public string? BuyerError { get; init; }
    public SalesInvoice? Invoice { get; init; }
    public InvoicingSettings Settings { get; init; } = new();
}

public sealed class GlobalPreview
{
    public int Year { get; init; }
    public int Month { get; init; }
    public List<SalesOrder> Orders { get; init; } = new();
    public decimal Total => Orders.Sum(OrderTotal);

    /// <summary>Ventas cuyo comprador dio RFC y no tienen factura individual: conviene facturarlas antes.</summary>
    public List<SalesOrder> WithBuyerRfc { get; init; } = new();
    public SalesInvoice? Existing { get; init; }

    public static decimal OrderTotal(SalesOrder order) =>
        order.Items.Count > 0 ? order.Items.Sum(i => i.UnitPrice * i.Quantity) : order.TotalAmount;
}

/// <summary>
/// Facturación CFDI 4.0 con Facturama: factura individual a quien la pide en Mercado Libre (datos fiscales de
/// <c>/orders/{id}/billing_info</c>), subida del XML y PDF a la compra (<c>/packs/{id}/fiscal_documents</c>),
/// factura global mensual al público en general con las ventas sin factura, y cancelación.
/// Nada se timbra sin que el vendedor lo pida.
/// </summary>
public sealed class InvoicingService
{
    public const string GlobalProductCode = "01010101";
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly FacturamaClient _facturama;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InvoicingService> _logger;

    public InvoicingService(
        AppDbContext context,
        FacturamaClient facturama,
        IMercadoLibreAuthService auth,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<InvoicingService> logger)
    {
        _context = context;
        _facturama = facturama;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public FacturamaClient Provider => _facturama;

    private string MlBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    public async Task<InvoicingSettings> GetSettingsAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var settings = await _context.InvoicingSettings.FirstOrDefaultAsync(s => s.OrganizationId == org, cancellationToken);
        if (settings is not null)
            return settings;
        settings = new InvoicingSettings { OrganizationId = org };
        _context.InvoicingSettings.Add(settings);
        await _context.SaveChangesAsync(cancellationToken);
        return settings;
    }

    /// <summary>Qué falta para poder facturar (vacío = listo).</summary>
    public List<string> MissingSetup(InvoicingSettings settings)
    {
        var missing = new List<string>();
        if (!_facturama.IsConfigured) missing.Add("las credenciales de Facturama en Render");
        if (string.IsNullOrWhiteSpace(settings.IssuerRfc)) missing.Add("tu RFC");
        if (string.IsNullOrWhiteSpace(settings.IssuerName)) missing.Add("tu nombre como en la Constancia");
        if (string.IsNullOrWhiteSpace(settings.FiscalRegime)) missing.Add("tu régimen fiscal");
        if (string.IsNullOrWhiteSpace(settings.ExpeditionZip)) missing.Add("tu código postal fiscal");
        if (string.IsNullOrWhiteSpace(settings.DefaultProductCode)) missing.Add("la clave SAT de tus productos");
        if (settings.CsdUploadedAt is null) missing.Add("tu CSD (.cer y .key)");
        return missing;
    }

    // ─────────────────────────────────────────────────────────────
    // CSD
    // ─────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string Message)> UploadCsdAsync(Guid org, byte[] certificate, byte[] privateKey, string password, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(org, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.IssuerRfc))
            return (false, "Primero guarda tu RFC.");
        if (certificate.Length == 0 || privateKey.Length == 0 || string.IsNullOrEmpty(password))
            return (false, "Sube el .cer, el .key y escribe la contraseña de la llave.");

        var (ok, message) = await _facturama.UploadCsdAsync(settings.IssuerRfc, certificate, privateKey, password, cancellationToken);
        settings.CsdStatus = ok ? message : message[..Math.Min(message.Length, 200)];
        if (ok)
            settings.CsdUploadedAt = DateTime.UtcNow;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return (ok, message);
    }

    // ─────────────────────────────────────────────────────────────
    // Datos del comprador
    // ─────────────────────────────────────────────────────────────

    /// <summary>Lee los datos de facturación que el comprador capturó en Mercado Libre.</summary>
    public async Task<(BuyerFiscalData? Data, string? Error)> BuyerDataAsync(Guid org, SalesOrder order, CancellationToken cancellationToken = default)
    {
        string token;
        try { token = await _auth.GetValidAccessTokenAsync(org, order.MarketplaceAccountId); }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "No token for billing info of order {Order}.", order.Id);
            return (null, "No hay sesión de Mercado Libre para esa cuenta.");
        }

        BuyerFiscalData? data = null;
        foreach (var version in new[] { "2", null })
        {
            var (ok, content) = await MlGetAsync(token, $"/orders/{Uri.EscapeDataString(order.ExternalOrderId)}/billing_info", version, cancellationToken);
            if (!ok)
                continue;
            data = ParseBillingInfo(content);
            if (data?.HasRfc == true)
                break;
        }
        return data is null ? (null, "Mercado Libre no regresó datos de facturación de esta venta.") : (data, null);
    }

    /// <summary>Lee las dos formas de respuesta (x-version 2 y la anterior con additional_info).</summary>
    public static BuyerFiscalData? ParseBillingInfo(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            // V2: { buyer: { billing_info: { name, last_name, identification, taxes, address } } }
            if (root.TryGetProperty("buyer", out var buyer) && buyer.TryGetProperty("billing_info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                var rfc = info.TryGetProperty("identification", out var id) ? Str(id, "number") : null;
                var name = Join(Str(info, "name"), Str(info, "last_name"));
                string? regime = null, use = null, personType = null;
                if (info.TryGetProperty("taxes", out var taxes) && taxes.ValueKind == JsonValueKind.Object)
                {
                    personType = Str(taxes, "contributor");
                    if (taxes.TryGetProperty("taxpayer_type", out var tt))
                        regime = RegimeFrom(Str(tt, "id")) ?? RegimeFrom(Str(tt, "description"));
                    if (taxes.TryGetProperty("cfdi", out var cfdi))
                        use = Str(cfdi, "id");
                }
                var zip = info.TryGetProperty("address", out var address) ? Str(address, "zip_code") : null;
                return new BuyerFiscalData(Clean(rfc), CleanName(name), regime, use, zip, personType);
            }

            // V1: { billing_info: { doc_type, doc_number, additional_info: [{type, value}] } }
            var v1 = root.TryGetProperty("billing_info", out var b) ? b : root;
            if (v1.ValueKind != JsonValueKind.Object)
                return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (v1.TryGetProperty("additional_info", out var additional) && additional.ValueKind == JsonValueKind.Array)
                foreach (var item in additional.EnumerateArray())
                    if (Str(item, "type") is { } type && Str(item, "value") is { } value)
                        values[type] = value;
            string? Find(params string[] keys) => keys.Select(k => values.FirstOrDefault(v => v.Key.Contains(k, StringComparison.OrdinalIgnoreCase)).Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var business = Find("BUSINESS_NAME", "SOCIAL");
            var personName = Join(Find("FIRST_NAME"), Find("LAST_NAME"));
            return new BuyerFiscalData(
                Clean(Str(v1, "doc_number")),
                CleanName(business ?? personName),
                RegimeFrom(Find("TAXPAYER_TYPE", "REGIME", "REGIMEN")),
                Find("CFDI"),
                Find("ZIP", "POSTAL"),
                Find("CONTRIBUTOR", "PERSON_TYPE"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Saca el código de régimen (601, 612, 626…) del id o la descripción.</summary>
    public static string? RegimeFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var code = Regex.Match(text, @"\b(6\d\d)\b");
        if (code.Success && SatCatalogs.FiscalRegimes.Any(r => r.Code == code.Value))
            return code.Value;
        var t = text.ToLowerInvariant();
        if (t.Contains("simplificado") || t.Contains("resico")) return "626";
        if (t.Contains("plataforma")) return "625";
        if (t.Contains("sin obligaciones")) return "616";
        if (t.Contains("incorporaci")) return "621";
        if (t.Contains("empresarial") || t.Contains("profesional")) return "612";
        if (t.Contains("sueldos") || t.Contains("salarios")) return "605";
        if (t.Contains("arrendamiento")) return "606";
        if (t.Contains("no lucrativ")) return "603";
        if (t.Contains("general de ley") || t.Contains("personas morales")) return "601";
        return null;
    }

    /// <summary>Revisa ventas pagadas recientes sin factura para ver si el comprador dio RFC (para avisar).</summary>
    public async Task<List<SalesOrder>> CheckBuyerRequestsAsync(Guid org, int limit = 15, CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var orders = await _context.SalesOrders
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= since &&
                        o.SalesInvoiceId == null && o.BillingCheckedAt == null)
            .OrderByDescending(o => o.DateCreated)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var found = new List<SalesOrder>();
        foreach (var order in orders)
        {
            var (data, _) = await BuyerDataAsync(org, order, cancellationToken);
            order.BillingCheckedAt = DateTime.UtcNow;
            if (data?.HasRfc == true)
            {
                order.BuyerRfc = data.Rfc;
                found.Add(order);
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        return found;
    }

    // ─────────────────────────────────────────────────────────────
    // Factura individual
    // ─────────────────────────────────────────────────────────────

    public async Task<InvoiceDraft?> DraftAsync(Guid org, int orderId, bool readBuyer, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == org, cancellationToken);
        if (order is null)
            return null;

        var orders = string.IsNullOrWhiteSpace(order.ExternalPackId)
            ? new List<SalesOrder> { order }
            : await _context.SalesOrders.Include(o => o.Items)
                .Where(o => o.OrganizationId == org && o.MarketplaceAccountId == order.MarketplaceAccountId &&
                            o.ExternalPackId == order.ExternalPackId && o.Status != "cancelled")
                .OrderBy(o => o.Id)
                .ToListAsync(cancellationToken);
        if (orders.Count == 0)
            orders.Add(order);

        var settings = await GetSettingsAsync(org, cancellationToken);
        var lines = await LinesAsync(org, orders, settings, cancellationToken);

        var invoiceId = orders.Select(o => o.SalesInvoiceId).FirstOrDefault(i => i is not null);
        var invoice = invoiceId is null ? null : await _context.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        BuyerFiscalData? buyer = null;
        string? error = null;
        if (readBuyer && (invoice is null || invoice.Kind == InvoiceKinds.Global))
        {
            (buyer, error) = await BuyerDataAsync(org, order, cancellationToken);
            order.BillingCheckedAt = DateTime.UtcNow;
            if (buyer?.HasRfc == true)
                order.BuyerRfc = buyer.Rfc;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new InvoiceDraft { Order = order, Orders = orders, Lines = lines, Buyer = buyer, BuyerError = error, Invoice = invoice, Settings = settings };
    }

    private async Task<List<InvoiceLine>> LinesAsync(Guid org, List<SalesOrder> orders, InvoicingSettings settings, CancellationToken cancellationToken)
    {
        var itemIds = orders.SelectMany(o => o.Items).Select(i => i.ExternalItemId).Distinct().ToList();
        var products = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.ExternalItemId != null && itemIds.Contains(p.ExternalItemId))
            .Select(p => new { p.ExternalItemId, p.ProductId, p.Product.SatProductCode })
            .ToListAsync(cancellationToken);
        return orders.SelectMany(o => o.Items).Select(i =>
        {
            var product = products.FirstOrDefault(p => p.ExternalItemId == i.ExternalItemId);
            return new InvoiceLine
            {
                ProductId = product?.ProductId,
                Title = i.Title ?? i.ExternalItemId,
                Sku = i.SellerSku,
                Quantity = Math.Max(1, i.Quantity),
                UnitPrice = i.UnitPrice,
                SatCode = product?.SatProductCode ?? settings.DefaultProductCode
            };
        }).ToList();
    }

    /// <summary>Timbra la factura de la venta (y su carrito), la liga a las ventas y la sube a Mercado Libre.</summary>
    public async Task<(bool Ok, string Message, SalesInvoice? Invoice)> IssueIndividualAsync(
        Guid org, int orderId, ReceiverInput receiver, IDictionary<int, string>? satCodes, bool rememberCodes, CancellationToken cancellationToken = default)
    {
        var draft = await DraftAsync(org, orderId, readBuyer: false, cancellationToken);
        if (draft is null)
            return (false, "No encontré esa venta.", null);
        var settings = draft.Settings;
        var missing = MissingSetup(settings);
        if (missing.Count > 0)
            return (false, "Para facturar falta: " + string.Join(", ", missing) + ".", null);
        if (draft.Orders.Any(o => o.Status != "paid"))
            return (false, "Solo se facturan ventas pagadas.", null);
        if (draft.Invoice is { Status: InvoiceStatus.Issued } existing)
            return (false, existing.Kind == InvoiceKinds.Global
                ? $"Esta venta ya va en la factura global {existing.Serie}{existing.Folio}. Para facturarla aparte, primero cancela y vuelve a emitir la global sin ella."
                : $"Esta venta ya tiene la factura {existing.Serie}{existing.Folio}.", null);

        var rfc = receiver.Rfc?.Trim().ToUpperInvariant();
        if (!SatCatalogs.LooksLikeRfc(rfc) || rfc == SatCatalogs.PublicRfc)
            return (false, "El RFC del comprador no es válido. Si no te dio RFC, la venta va en la factura global del mes.", null);
        if (string.IsNullOrWhiteSpace(receiver.Name) || string.IsNullOrWhiteSpace(receiver.FiscalRegime) ||
            string.IsNullOrWhiteSpace(receiver.CfdiUse) || receiver.Zip is not { Length: 5 })
            return (false, "Falta el nombre, régimen, uso del CFDI o código postal del comprador.", null);

        // Claves SAT capturadas en el formulario.
        if (satCodes is not null)
            foreach (var line in draft.Lines)
                if (line.ProductId is { } pid && satCodes.TryGetValue(pid, out var code) && Regex.IsMatch(code ?? "", @"^\d{8}$"))
                    line.SatCode = code;
        if (draft.Lines.Any(l => !Regex.IsMatch(l.SatCode ?? "", @"^\d{8}$")))
            return (false, "Cada producto necesita su clave SAT de 8 dígitos.", null);

        var folio = await ClaimFolioAsync(settings.Id, global: false, cancellationToken);
        var payload = BasePayload(settings, settings.Serie, folio);
        payload["Receiver"] = new JsonObject
        {
            ["Rfc"] = rfc,
            ["Name"] = CleanName(receiver.Name),
            ["CfdiUse"] = receiver.CfdiUse,
            ["FiscalRegime"] = receiver.FiscalRegime,
            ["TaxZipCode"] = receiver.Zip
        };
        var items = new JsonArray();
        decimal subtotal = 0, tax = 0;
        foreach (var line in draft.Lines)
        {
            var (item, s, t) = Item(line.SatCode!, "H87", "Pieza", line.Sku ?? draft.Order.ExternalOrderId, Short(line.Title, 1000), line.Quantity, line.UnitPrice, settings.IvaRate);
            items.Add(item);
            subtotal += s;
            tax += t;
        }
        payload["Items"] = items;

        var result = await _facturama.IssueAsync(payload, cancellationToken);
        if (!result.Ok)
        {
            await ReleaseFolioAsync(settings.Id, global: false, folio, cancellationToken);
            return (false, "No se timbró: " + result.Error, null);
        }

        var invoice = new SalesInvoice
        {
            OrganizationId = org,
            Kind = InvoiceKinds.Individual,
            ProviderId = result.Id,
            Uuid = result.Uuid,
            Serie = settings.Serie,
            Folio = folio,
            ReceiverRfc = rfc!,
            ReceiverName = CleanName(receiver.Name)!,
            CfdiUse = receiver.CfdiUse!,
            Subtotal = subtotal,
            Tax = tax,
            Total = subtotal + tax,
            OrdersCount = draft.Orders.Count,
            MarketplaceAccountId = draft.Order.MarketplaceAccountId,
            MercadoLibrePackId = draft.Order.ExternalPackId ?? draft.Order.ExternalOrderId
        };
        _context.SalesInvoices.Add(invoice);
        await _context.SaveChangesAsync(cancellationToken);
        foreach (var order in draft.Orders)
        {
            order.SalesInvoiceId = invoice.Id;
            order.BuyerRfc = rfc;
        }
        if (rememberCodes)
        {
            var codes = draft.Lines.Where(l => l.ProductId is not null).GroupBy(l => l.ProductId!.Value).ToDictionary(g => g.Key, g => g.First().SatCode);
            var products = await _context.Products.Where(p => p.OrganizationId == org && codes.Keys.Contains(p.Id)).ToListAsync(cancellationToken);
            foreach (var product in products)
                product.SatProductCode = codes[product.Id];
        }
        await _context.SaveChangesAsync(cancellationToken);

        var message = $"Factura {invoice.Serie}{invoice.Folio} timbrada para {invoice.ReceiverName} por {invoice.Total.ToString("C2", Mx)}.";
        if (settings.UploadToMercadoLibre)
        {
            var (uploaded, uploadMessage) = await UploadToMercadoLibreAsync(org, invoice.Id, cancellationToken);
            message += uploaded ? " Ya está en la compra de Mercado Libre." : " " + uploadMessage;
        }
        return (true, message, invoice);
    }

    /// <summary>Factura con los datos que el comprador dio en Mercado Libre, sin editar (botón de Telegram).</summary>
    public async Task<(bool Ok, string Message)> IssueFromBuyerDataAsync(Guid org, int orderId, CancellationToken cancellationToken = default)
    {
        var draft = await DraftAsync(org, orderId, readBuyer: true, cancellationToken);
        if (draft is null)
            return (false, "No encontré esa venta.");
        var buyer = draft.Buyer;
        if (buyer?.HasRfc != true)
            return (false, "El comprador no dio RFC en Mercado Libre. Revísalo en la app.");
        var receiver = new ReceiverInput
        {
            Rfc = buyer.Rfc,
            Name = buyer.Name,
            FiscalRegime = buyer.FiscalRegime,
            CfdiUse = buyer.CfdiUse ?? "G03",
            Zip = buyer.Zip
        };
        if (receiver.FiscalRegime is null || receiver.Zip is not { Length: 5 } || string.IsNullOrWhiteSpace(receiver.Name))
            return (false, "Faltan datos del comprador (régimen, nombre o código postal). Complétalos en la app.");
        var (ok, message, _) = await IssueIndividualAsync(org, orderId, receiver, null, rememberCodes: false, cancellationToken);
        return (ok, message);
    }

    // ─────────────────────────────────────────────────────────────
    // Factura global
    // ─────────────────────────────────────────────────────────────

    public async Task<GlobalPreview> GlobalPreviewAsync(Guid org, int year, int month, CancellationToken cancellationToken = default)
    {
        var (from, to) = MonthRange(year, month);
        var orders = await _context.SalesOrders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= from && o.DateCreated < to && o.SalesInvoiceId == null)
            .OrderBy(o => o.DateCreated)
            .ToListAsync(cancellationToken);
        var existing = await _context.SalesInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == org && i.Kind == InvoiceKinds.Global && i.PeriodYear == year && i.PeriodMonth == month && i.Status == InvoiceStatus.Issued)
            .OrderByDescending(i => i.IssuedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return new GlobalPreview
        {
            Year = year,
            Month = month,
            Orders = orders.Where(o => o.BuyerRfc is null).ToList(),
            WithBuyerRfc = orders.Where(o => o.BuyerRfc is not null).ToList(),
            Existing = existing
        };
    }

    /// <summary>
    /// Factura global mensual (público en general, XAXX010101000, S01, 616) con un concepto por venta sin factura.
    /// Las ventas cuyo comprador dio RFC se dejan fuera para facturarlas aparte.
    /// </summary>
    public async Task<(bool Ok, string Message, SalesInvoice? Invoice)> IssueGlobalAsync(Guid org, int year, int month, bool includeBuyersWithRfc = false, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(org, cancellationToken);
        var missing = MissingSetup(settings).Where(m => !m.Contains("clave SAT")).ToList();
        if (missing.Count > 0)
            return (false, "Para facturar falta: " + string.Join(", ", missing) + ".", null);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));
        if (new DateOnly(year, month, 1) > new DateOnly(today.Year, today.Month, 1))
            return (false, "Ese mes todavía no empieza.", null);

        var preview = await GlobalPreviewAsync(org, year, month, cancellationToken);
        var orders = includeBuyersWithRfc ? preview.Orders.Concat(preview.WithBuyerRfc).ToList() : preview.Orders;
        if (orders.Count == 0)
            return (false, "No hay ventas sin factura en ese mes.", null);

        var folio = await ClaimFolioAsync(settings.Id, global: true, cancellationToken);
        var payload = BasePayload(settings, settings.GlobalSerie, folio);
        payload["GlobalInformation"] = new JsonObject
        {
            ["Periodicity"] = "04",
            ["Months"] = month.ToString("00", CultureInfo.InvariantCulture),
            ["Year"] = year.ToString(CultureInfo.InvariantCulture)
        };
        payload["Receiver"] = new JsonObject
        {
            ["Rfc"] = SatCatalogs.PublicRfc,
            ["Name"] = "PUBLICO EN GENERAL",
            ["CfdiUse"] = "S01",
            ["FiscalRegime"] = "616",
            ["TaxZipCode"] = settings.ExpeditionZip
        };
        var items = new JsonArray();
        decimal subtotal = 0, tax = 0;
        foreach (var order in orders)
        {
            var (item, s, t) = Item(GlobalProductCode, "ACT", "Actividad", order.ExternalOrderId, "Venta", 1, GlobalPreview.OrderTotal(order), settings.IvaRate);
            items.Add(item);
            subtotal += s;
            tax += t;
        }
        payload["Items"] = items;

        var result = await _facturama.IssueAsync(payload, cancellationToken);
        if (!result.Ok)
        {
            await ReleaseFolioAsync(settings.Id, global: true, folio, cancellationToken);
            return (false, "No se timbró la global: " + result.Error, null);
        }

        var invoice = new SalesInvoice
        {
            OrganizationId = org,
            Kind = InvoiceKinds.Global,
            ProviderId = result.Id,
            Uuid = result.Uuid,
            Serie = settings.GlobalSerie,
            Folio = folio,
            ReceiverRfc = SatCatalogs.PublicRfc,
            ReceiverName = "PUBLICO EN GENERAL",
            CfdiUse = "S01",
            Subtotal = subtotal,
            Tax = tax,
            Total = subtotal + tax,
            PeriodYear = year,
            PeriodMonth = month,
            OrdersCount = orders.Count
        };
        _context.SalesInvoices.Add(invoice);
        await _context.SaveChangesAsync(cancellationToken);
        var ids = orders.Select(o => o.Id).ToList();
        await _context.SalesOrders.Where(o => ids.Contains(o.Id) && o.OrganizationId == org)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.SalesInvoiceId, invoice.Id), cancellationToken);

        var monthName = new DateTime(year, month, 1).ToString("MMMM yyyy", Mx);
        return (true, $"Factura global {invoice.Serie}{invoice.Folio} de {monthName}: {orders.Count} ventas por {invoice.Total.ToString("C2", Mx)}.", invoice);
    }

    // ─────────────────────────────────────────────────────────────
    // Cancelar, descargar, subir a Mercado Libre
    // ─────────────────────────────────────────────────────────────

    /// <summary>Cancela ante el SAT. Las ventas quedan sin factura (para refacturar o ir en la global).</summary>
    public async Task<(bool Ok, string Message)> CancelAsync(Guid org, int invoiceId, string motive, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.OrganizationId == org, cancellationToken);
        if (invoice is null)
            return (false, "No encontré esa factura.");
        if (invoice.Status == InvoiceStatus.Cancelled)
            return (false, "Esa factura ya estaba cancelada.");
        if (invoice.ProviderId is null)
            return (false, "Esa factura no tiene número de Facturama.");
        if (motive is not ("02" or "03" or "04"))
            motive = "02";

        var (ok, message) = await _facturama.CancelAsync(invoice.ProviderId, motive, null, cancellationToken);
        if (!ok)
            return (false, "No se canceló: " + message);

        invoice.Status = InvoiceStatus.Cancelled;
        invoice.CancelledAt = DateTime.UtcNow;
        invoice.CancelMotive = motive;
        await _context.SaveChangesAsync(cancellationToken);
        await _context.SalesOrders.Where(o => o.OrganizationId == org && o.SalesInvoiceId == invoice.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.SalesInvoiceId, (int?)null), cancellationToken);

        if (invoice.UploadedToMercadoLibreAt is not null && invoice.MercadoLibrePackId is not null && invoice.MarketplaceAccountId is { } accountId)
        {
            try
            {
                var token = await _auth.GetValidAccessTokenAsync(org, accountId);
                var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{MlBase}/packs/{Uri.EscapeDataString(invoice.MercadoLibrePackId)}/fiscal_documents");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    message += " También la quité de la compra en Mercado Libre.";
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Removing fiscal documents of invoice {Invoice} failed.", invoice.Id);
            }
        }
        return (true, $"{invoice.Serie}{invoice.Folio}: {message}");
    }

    public async Task<(byte[]? Bytes, string FileName, string ContentType)> DownloadAsync(Guid org, int invoiceId, string format, CancellationToken cancellationToken = default)
    {
        format = format == "xml" ? "xml" : "pdf";
        var invoice = await _context.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.OrganizationId == org, cancellationToken);
        if (invoice?.ProviderId is null)
            return (null, string.Empty, string.Empty);
        var bytes = await _facturama.DownloadAsync(invoice.ProviderId, format, cancellationToken);
        return (bytes, $"{invoice.Serie}{invoice.Folio}-{invoice.ReceiverRfc}.{format}", format == "xml" ? "application/xml" : "application/pdf");
    }

    /// <summary>Sube el XML y el PDF a la compra para que el comprador la descargue en Mercado Libre.</summary>
    public async Task<(bool Ok, string Message)> UploadToMercadoLibreAsync(Guid org, int invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.OrganizationId == org, cancellationToken);
        if (invoice is null || invoice.Kind != InvoiceKinds.Individual || invoice.MercadoLibrePackId is null || invoice.MarketplaceAccountId is null || invoice.ProviderId is null)
            return (false, "Esa factura no es de una compra de Mercado Libre.");
        if (invoice.Status != InvoiceStatus.Issued)
            return (false, "La factura está cancelada.");

        var pdf = await _facturama.DownloadAsync(invoice.ProviderId, "pdf", cancellationToken);
        var xml = await _facturama.DownloadAsync(invoice.ProviderId, "xml", cancellationToken);
        if (pdf is null || xml is null)
            return await UploadFailedAsync(invoice, "No pude descargar el PDF o XML de Facturama para subirlo a Mercado Libre.", cancellationToken);
        if (pdf.Length > 1_000_000)
            return await UploadFailedAsync(invoice, "El PDF pesa más de 1 MB y Mercado Libre no lo acepta; mándalo por mensaje.", cancellationToken);

        try
        {
            var token = await _auth.GetValidAccessTokenAsync(org, invoice.MarketplaceAccountId.Value);
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{MlBase}/packs/{Uri.EscapeDataString(invoice.MercadoLibrePackId)}/fiscal_documents");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var form = new MultipartFormDataContent();
            var pdfContent = new ByteArrayContent(pdf);
            pdfContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(pdfContent, "fiscal_document", $"{invoice.Serie}{invoice.Folio}.pdf");
            var xmlContent = new ByteArrayContent(xml);
            xmlContent.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            form.Add(xmlContent, "fiscal_document", $"{invoice.Serie}{invoice.Folio}.xml");
            request.Content = form;
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Fiscal documents upload for {Pack} returned {Status}: {Body}", invoice.MercadoLibrePackId, (int)response.StatusCode, content.Length > 300 ? content[..300] : content);
                var reason = (int)response.StatusCode switch
                {
                    409 => "Mercado Libre ya tiene una factura en esa compra.",
                    403 => "Mercado Libre no dejó subir la factura (permiso de la app o la compra no es tuya).",
                    _ => "Mercado Libre no aceptó la factura (" + (int)response.StatusCode + ")."
                };
                return await UploadFailedAsync(invoice, reason, cancellationToken);
            }
            invoice.UploadedToMercadoLibreAt = DateTime.UtcNow;
            invoice.UploadError = null;
            await _context.SaveChangesAsync(cancellationToken);
            return (true, "Factura subida a la compra en Mercado Libre.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Fiscal documents upload for invoice {Invoice} failed.", invoice.Id);
            return await UploadFailedAsync(invoice, "Mercado Libre no respondió al subir la factura.", cancellationToken);
        }
    }

    private async Task<(bool, string)> UploadFailedAsync(SalesInvoice invoice, string reason, CancellationToken cancellationToken)
    {
        invoice.UploadError = reason.Length > 300 ? reason[..300] : reason;
        await _context.SaveChangesAsync(cancellationToken);
        return (false, reason);
    }

    // ─────────────────────────────────────────────────────────────
    // Armado del CFDI
    // ─────────────────────────────────────────────────────────────

    private static JsonObject BasePayload(InvoicingSettings settings, string serie, int folio) => new()
    {
        ["NameId"] = "1",
        ["Serie"] = serie,
        ["Folio"] = folio.ToString(CultureInfo.InvariantCulture),
        ["CfdiType"] = "I",
        ["Currency"] = "MXN",
        ["PaymentForm"] = settings.PaymentForm,
        ["PaymentMethod"] = "PUE",
        ["ExpeditionPlace"] = settings.ExpeditionZip,
        ["Exportation"] = "01",
        ["Date"] = DateTime.UtcNow.AddHours(-7).AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        ["Issuer"] = new JsonObject
        {
            ["Rfc"] = settings.IssuerRfc,
            ["Name"] = CleanName(settings.IssuerName),
            ["FiscalRegime"] = settings.FiscalRegime
        }
    };

    /// <summary>
    /// Un concepto con IVA trasladado. El precio de Mercado Libre ya trae IVA: se separa la base
    /// (precio ÷ (1 + tasa)) y el impuesto se calcula sobre la base, como valida el SAT.
    /// </summary>
    public static (JsonObject Item, decimal Subtotal, decimal Tax) Item(string productCode, string unitCode, string unit, string identification, string description, int quantity, decimal unitPriceWithTax, decimal ivaRate)
    {
        var unitPrice = Math.Round(unitPriceWithTax / (1 + ivaRate), 6, MidpointRounding.AwayFromZero);
        var subtotal = Math.Round(unitPrice * quantity, 2, MidpointRounding.AwayFromZero);
        var tax = Math.Round(subtotal * ivaRate, 2, MidpointRounding.AwayFromZero);
        var item = new JsonObject
        {
            ["ProductCode"] = productCode,
            ["IdentificationNumber"] = Short(identification, 100),
            ["Description"] = description,
            ["Unit"] = unit,
            ["UnitCode"] = unitCode,
            ["UnitPrice"] = unitPrice,
            ["Quantity"] = quantity,
            ["Subtotal"] = subtotal,
            ["TaxObject"] = "02",
            ["Taxes"] = new JsonArray
            {
                new JsonObject
                {
                    ["Total"] = tax,
                    ["Name"] = "IVA",
                    ["Base"] = subtotal,
                    ["Rate"] = ivaRate,
                    ["IsRetention"] = false
                }
            },
            ["Total"] = subtotal + tax
        };
        return (item, subtotal, tax);
    }

    private async Task<int> ClaimFolioAsync(int settingsId, bool global, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var current = await _context.InvoicingSettings.AsNoTracking().Where(s => s.Id == settingsId)
                .Select(s => global ? s.NextGlobalFolio : s.NextFolio).FirstAsync(cancellationToken);
            var updated = global
                ? await _context.InvoicingSettings.Where(s => s.Id == settingsId && s.NextGlobalFolio == current)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextGlobalFolio, current + 1), cancellationToken)
                : await _context.InvoicingSettings.Where(s => s.Id == settingsId && s.NextFolio == current)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextFolio, current + 1), cancellationToken);
            if (updated == 1)
                return current;
        }
        throw new InvalidOperationException("No pude apartar el folio.");
    }

    /// <summary>Si no se timbró, regresa el folio (solo si nadie tomó el siguiente).</summary>
    private async Task ReleaseFolioAsync(int settingsId, bool global, int folio, CancellationToken cancellationToken)
    {
        if (global)
            await _context.InvoicingSettings.Where(s => s.Id == settingsId && s.NextGlobalFolio == folio + 1)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextGlobalFolio, folio), cancellationToken);
        else
            await _context.InvoicingSettings.Where(s => s.Id == settingsId && s.NextFolio == folio + 1)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextFolio, folio), cancellationToken);
    }

    public static (DateTime From, DateTime To) MonthRange(int year, int month)
    {
        var first = new DateTime(year, month, 1, 7, 0, 0, DateTimeKind.Utc); // 00:00 en Juárez
        return (first, first.AddMonths(1));
    }

    private async Task<(bool Ok, string Content)> MlGetAsync(string token, string path, string? version, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, MlBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (version is not null)
                request.Headers.TryAddWithoutValidation("x-version", version);
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return (response.IsSuccessStatusCode, content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Mercado Libre GET {Path} failed.", path);
            return (false, string.Empty);
        }
    }

    private static string? Clean(string? rfc) =>
        string.IsNullOrWhiteSpace(rfc) ? null : Regex.Replace(rfc, @"[\s-]", string.Empty).ToUpperInvariant();

    /// <summary>Nombre en mayúsculas y sin régimen de capital (CFDI 4.0 lo pide como en la Constancia).</summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var upper = Regex.Replace(name.Trim().ToUpperInvariant(), @"\s+", " ");
        upper = Regex.Replace(upper, @",?\s+(S\.?\s?A\.?\s?P\.?\s?I\.?|S\.?\s?A\.?|S\.?\s?DE\s?R\.?\s?L\.?|S\.?\s?C\.?)(\s+DE\s+C\.?\s?V\.?)?\.?$", string.Empty);
        return upper.Trim(' ', ',');
    }

    private static string? Join(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a.Trim() + " " + b.Trim();

    private static string Short(string text, int max) => text.Length <= max ? text : text[..max];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() is { Length: > 0 } s ? s : null : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;
}
