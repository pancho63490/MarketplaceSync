using System.Globalization;
using System.Text.Json;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Helpers;
using MarketplaceSync.Web.Models.Inventory;
using MarketplaceSync.Web.Models.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

/// <summary>Un renglón del Excel con cambios.</summary>
public sealed class BulkEditRow
{
    public int Line { get; set; }
    public int PublicationId { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public decimal? OldPrice { get; set; }
    public decimal? NewPrice { get; set; }
    public int? OldStock { get; set; }
    public int? NewStock { get; set; }
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }

    /// <summary>El stock lo controla Inventario (una sola variante): se ajusta ahí y se manda a Mercado Libre.</summary>
    public int? VariantId { get; set; }

    public decimal? ProfitNow { get; set; }
    public decimal? ProfitNew { get; set; }
    public List<string> Warnings { get; set; } = new();

    /// <summary>No se puede aplicar (se muestra pero no se manda).</summary>
    public string? Error { get; set; }

    /// <summary>El vendedor lo dejó marcado en la vista previa.</summary>
    public bool Selected { get; set; } = true;

    /// <summary>done, failed o null (pendiente).</summary>
    public string? Result { get; set; }
    public string? ResultMessage { get; set; }

    public bool HasChanges => NewPrice is not null || NewStock is not null || NewStatus is not null;
}

/// <summary>
/// Cambios masivos por Excel: se baja un archivo con todas las publicaciones (precio, stock, costo y ganancia),
/// se llenan las columnas "Precio nuevo", "Stock nuevo" y "Estado nuevo", se sube, se revisa la vista previa
/// con la ganancia al precio nuevo y se aplica en segundo plano.
/// </summary>
public sealed class BulkEditService
{
    public const int MaxRows = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    public static readonly string[] Headers =
    {
        "ID", "MLM", "SKU", "Título", "Estado", "Precio", "Stock", "Costo US$", "Ganas por pieza", "Precio sugerido",
        "Precio nuevo", "Stock nuevo", "Estado nuevo"
    };

    private readonly AppDbContext _context;
    private readonly ICrossBorderPricingService _pricing;
    private readonly PublicationEconomics _economics;
    private readonly MercadoLibreItemEditorService _editor;
    private readonly IStockSyncScheduler _stockSync;
    private readonly ILogger<BulkEditService> _logger;

    public BulkEditService(
        AppDbContext context,
        ICrossBorderPricingService pricing,
        PublicationEconomics economics,
        MercadoLibreItemEditorService editor,
        IStockSyncScheduler stockSync,
        ILogger<BulkEditService> logger)
    {
        _context = context;
        _pricing = pricing;
        _economics = economics;
        _editor = editor;
        _stockSync = stockSync;
        _logger = logger;
    }

    public static List<BulkEditRow> Rows(BulkEditImport import) =>
        JsonSerializer.Deserialize<List<BulkEditRow>>(import.RowsJson, Json) ?? new();

    private static void SetRows(BulkEditImport import, List<BulkEditRow> rows) =>
        import.RowsJson = JsonSerializer.Serialize(rows, Json);

    // ─────────────────────────────────────────────────────────────
    // Bajar
    // ─────────────────────────────────────────────────────────────

    public async Task<byte[]> ExportAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product).ThenInclude(p => p.Variants)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null &&
                        p.Status != "closed")
            .OrderBy(p => p.Status == "active" ? 0 : 1).ThenBy(p => p.Product.Title)
            .ToListAsync(cancellationToken);

        var xlsx = new SimpleXlsxWriter();
        var sheet = xlsx.AddSheet("Publicaciones", 8, 16, 16, 50, 10, 12, 8, 11, 14, 14, 14, 12, 13);
        sheet.Add(Headers.Select(h => new SimpleXlsxWriter.Cell(h, SimpleXlsxWriter.CellStyle.Header)).ToList());
        foreach (var p in publications)
        {
            var money = _economics.Calculate(p, p.Product, settings);
            var variants = p.Product.Variants.Where(v => v.IsActive).ToList();
            var stock = variants.Count > 0 ? variants.Sum(v => Math.Max(0, v.Stock)) : p.Stock;
            sheet.Add(new List<SimpleXlsxWriter.Cell>
            {
                new(p.Id, SimpleXlsxWriter.CellStyle.Integer),
                new(p.ExternalItemId),
                new(variants.Count == 1 ? variants[0].Sku : variants.Count > 1 ? $"{variants.Count} variantes" : null),
                new(p.Product.Title),
                new(StatusLabel(p.Status)),
                new(p.Price, SimpleXlsxWriter.CellStyle.Money),
                new(stock, SimpleXlsxWriter.CellStyle.Integer),
                new(PublicationEconomics.CostKnown(p.Product) ? p.Product.SourcePrice : null, SimpleXlsxWriter.CellStyle.Money),
                new(money?.Profit.NetProfitMxn, SimpleXlsxWriter.CellStyle.Money),
                new(money?.SuggestedPriceMxn is { } s ? Math.Round(s, 0) : null, SimpleXlsxWriter.CellStyle.Money),
                new(null), new(null), new(null)
            });
        }

        var help = xlsx.AddSheet("Instrucciones", 110);
        foreach (var line in new[]
        {
            "Cómo usar este archivo",
            "1. Llena solo las columnas «Precio nuevo», «Stock nuevo» y «Estado nuevo» (activa o pausada). Deja vacío lo que no quieras cambiar.",
            "2. No cambies la columna ID: con ella la app sabe qué publicación es. Puedes borrar renglones o columnas que no uses.",
            "3. Súbelo en Publicados → Subir Excel. Antes de mandar nada a Mercado Libre verás una vista previa con la ganancia a cada precio nuevo.",
            "«Ganas por pieza» es con el precio de hoy: costo puesto en Juárez, comisión, envío y retención (Costos de cruce).",
            "«Precio sugerido» es el que deja tu ganancia mínima. Si la publicación tiene variantes, el stock se cambia en Inventario.",
            $"Generado el {DateTime.UtcNow.AddHours(-7):dd/MM/yyyy HH:mm} (hora de Juárez)."
        })
            help.Add(new List<SimpleXlsxWriter.Cell> { new(line, line.StartsWith("Cómo") ? SimpleXlsxWriter.CellStyle.Header : SimpleXlsxWriter.CellStyle.Text) });
        return xlsx.Build();
    }

    // ─────────────────────────────────────────────────────────────
    // Subir y revisar
    // ─────────────────────────────────────────────────────────────

    public async Task<(BulkEditImport? Import, string? Error)> ParseAsync(Guid org, Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        List<List<string>> table;
        try
        {
            table = SimpleXlsxReader.Read(stream, fileName);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or IOException)
        {
            _logger.LogInformation(exception, "Unreadable bulk edit file {File}.", fileName);
            return (null, "No pude leer el archivo. Súbelo como .xlsx (Excel) o .csv.");
        }

        var headerIndex = table.FindIndex(r => r.Any(c => Normalize(c) == "id"));
        if (headerIndex < 0)
            return (null, "No encontré la columna «ID». Usa el Excel que baja la app (Publicados → Bajar Excel).");
        var header = table[headerIndex].Select(Normalize).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var idCol = Col("id");
        var priceCol = Col("precio nuevo", "nuevo precio");
        var stockCol = Col("stock nuevo", "nuevo stock");
        var statusCol = Col("estado nuevo", "nuevo estado");
        if (priceCol < 0 && stockCol < 0 && statusCol < 0)
            return (null, "El archivo no tiene las columnas «Precio nuevo», «Stock nuevo» ni «Estado nuevo».");

        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product).ThenInclude(p => p.Variants)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.ExternalItemId != null)
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var rows = new List<BulkEditRow>();
        var seen = new HashSet<int>();
        for (var i = headerIndex + 1; i < table.Count; i++)
        {
            var cells = table[i];
            string Cell(int col) => col >= 0 && col < cells.Count ? cells[col].Trim() : string.Empty;
            var priceText = Cell(priceCol);
            var stockText = Cell(stockCol);
            var statusText = Cell(statusCol);
            if (priceText.Length == 0 && stockText.Length == 0 && statusText.Length == 0)
                continue;

            var row = new BulkEditRow { Line = i + 1 };
            rows.Add(row);
            if (rows.Count > MaxRows)
                return (null, $"Son más de {MaxRows} renglones con cambios. Divide el archivo en partes.");

            if (!int.TryParse(Cell(idCol).Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || !publications.TryGetValue(id, out var publication))
            {
                row.Title = Cell(Col("título", "titulo")) is { Length: > 0 } t ? t : "(sin título)";
                row.Error = "No encontré esa publicación (revisa la columna ID).";
                continue;
            }
            if (!seen.Add(id))
            {
                row.Title = publication.Product.Title;
                row.Error = "La publicación viene repetida en el archivo; se usa el primer renglón.";
                continue;
            }

            var variants = publication.Product.Variants.Where(v => v.IsActive).ToList();
            row.PublicationId = id;
            row.ItemId = publication.ExternalItemId!;
            row.Title = publication.Product.Title;
            row.OldPrice = publication.Price;
            row.OldStatus = publication.Status;
            row.OldStock = variants.Count > 0 ? variants.Sum(v => Math.Max(0, v.Stock)) : publication.Stock;
            if (publication.Status == "closed")
            {
                row.Error = "La publicación está finalizada.";
                continue;
            }

            // Precio
            if (priceText.Length > 0)
            {
                if (!TryMoney(priceText, out var price) || price < 1)
                    row.Error = $"«{priceText}» no es un precio.";
                else if (price != publication.Price)
                {
                    row.NewPrice = Math.Round(price, 2);
                    if (publication.Price is > 0 && (price > publication.Price * 2 || price < publication.Price / 2))
                        row.Warnings.Add($"Cambio grande: de {publication.Price.Value.ToString("C0", Mx)} a {price.ToString("C0", Mx)}.");
                    if (publication.PromotionType is not null && price > (publication.Price ?? 0))
                        row.Warnings.Add("Tiene promoción: al subir el precio, Mercado Libre le quita el descuento.");
                }
            }

            // Stock
            if (stockText.Length > 0 && row.Error is null)
            {
                if (!int.TryParse(stockText.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock) || stock < 0 || stock > 100_000)
                    row.Error = $"«{stockText}» no es un stock.";
                else if (variants.Count > 1)
                    row.Error = "Tiene varias variantes: cambia el stock en Inventario.";
                else if (stock != row.OldStock)
                {
                    row.NewStock = stock;
                    row.VariantId = variants.Count == 1 ? variants[0].Id : null;
                }
            }

            // Estado
            if (statusText.Length > 0 && row.Error is null)
            {
                var status = Normalize(statusText) switch
                {
                    "activa" or "activo" or "activar" or "active" or "si" or "sí" => "active",
                    "pausada" or "pausado" or "pausar" or "paused" or "no" => "paused",
                    _ => null
                };
                if (status is null)
                    row.Error = $"Estado «{statusText}»: escribe activa o pausada.";
                else if (status != publication.Status)
                    row.NewStatus = status;
            }

            // Ganancia
            var now = _economics.Calculate(publication, publication.Product, settings);
            row.ProfitNow = now?.Profit.NetProfitMxn;
            if (row.NewPrice is { } np)
            {
                var atNew = _economics.Calculate(publication, publication.Product, settings, np);
                row.ProfitNew = atNew?.Profit.NetProfitMxn;
                if (atNew is null)
                    row.Warnings.Add("Sin costo capturado: no sé cuánto ganas.");
                else if (atNew.Profit.NetProfitMxn < 0)
                    row.Warnings.Add($"A ese precio pierdes {(-atNew.Profit.NetProfitMxn).ToString("C0", Mx)} por pieza.");
                else if (atNew.Profit.NetProfitMxn < settings.MinProfitAmountMxn)
                    row.Warnings.Add($"Gana menos de tu mínimo ({settings.MinProfitAmountMxn.ToString("C0", Mx)}).");
            }

            if (row.Error is null && !row.HasChanges)
                row.Error = "Sin cambios (ya tiene esos valores).";
            row.Selected = row.Error is null && !(row.ProfitNew < 0);
        }

        if (rows.Count == 0)
            return (null, "El archivo no trae cambios: llena «Precio nuevo», «Stock nuevo» o «Estado nuevo».");

        var import = new BulkEditImport
        {
            OrganizationId = org,
            FileName = Path.GetFileName(fileName).Length > 200 ? Path.GetFileName(fileName)[..200] : Path.GetFileName(fileName),
            Total = rows.Count(r => r.Error is null)
        };
        SetRows(import, rows);
        _context.BulkEditImports.Add(import);
        await _context.SaveChangesAsync(cancellationToken);
        return (import, null);
    }

    public Task<BulkEditImport?> LoadAsync(Guid org, int id, CancellationToken cancellationToken = default) =>
        _context.BulkEditImports.FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == org, cancellationToken);

    /// <summary>Guarda qué renglones dejó marcados y pasa a "aplicando" (el trabajo lo hace <see cref="ApplyAsync"/>).</summary>
    public async Task<(bool Ok, string Message)> StartAsync(Guid org, int id, IReadOnlyCollection<int> selectedLines, CancellationToken cancellationToken = default)
    {
        var import = await LoadAsync(org, id, cancellationToken);
        if (import is null)
            return (false, "No encontré ese archivo.");
        if (import.Status != BulkEditStatus.Preview)
            return (false, "Esos cambios ya se aplicaron.");
        var rows = Rows(import);
        foreach (var row in rows)
            row.Selected = row.Error is null && selectedLines.Contains(row.Line);
        var count = rows.Count(r => r.Selected);
        if (count == 0)
            return (false, "Marca al menos un cambio.");

        // Solo una vez: si dos clics llegan juntos, solo uno arranca.
        SetRows(import, rows);
        import.Total = count;
        await _context.SaveChangesAsync(cancellationToken);
        var claimed = await _context.BulkEditImports
            .Where(i => i.Id == id && i.Status == BulkEditStatus.Preview)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, BulkEditStatus.Applying).SetProperty(i => i.StartedAt, DateTime.UtcNow), cancellationToken);
        return claimed == 1 ? (true, $"Aplicando {count} {(count == 1 ? "cambio" : "cambios")} en Mercado Libre…") : (false, "Esos cambios ya se están aplicando.");
    }

    // ─────────────────────────────────────────────────────────────
    // Aplicar (Hangfire)
    // ─────────────────────────────────────────────────────────────

    public async Task ApplyAsync(Guid org, int id, CancellationToken cancellationToken)
    {
        var import = await LoadAsync(org, id, cancellationToken);
        if (import is null || import.Status != BulkEditStatus.Applying)
            return;
        var rows = Rows(import);
        var touchedProducts = new HashSet<int>();

        foreach (var row in rows.Where(r => r.Selected && r.Result is null))
        {
            var messages = new List<string>();
            var failed = false;
            try
            {
                // Precio y stock de Mercado Libre van juntos en un solo cambio.
                var edit = new ItemEdit();
                var needsEdit = false;
                if (row.NewPrice is { } price)
                {
                    edit.Price = price.ToString(CultureInfo.InvariantCulture);
                    needsEdit = true;
                }
                if (row.NewStock is { } stock && row.VariantId is null)
                {
                    edit.Quantity = stock;
                    needsEdit = true;
                }
                if (needsEdit)
                {
                    var result = await _editor.UpdateAsync(org, row.PublicationId, edit, cancellationToken);
                    if (result.Ok) messages.Add(result.Changed.Count > 0 ? "Cambié " + string.Join(" y ", result.Changed) : "Ya estaba así");
                    else { failed = true; messages.AddRange(result.Errors); }
                }

                // Stock que controla Inventario.
                if (row.NewStock is { } appStock && row.VariantId is { } variantId)
                {
                    var productId = await AdjustInventoryAsync(org, variantId, appStock, cancellationToken);
                    if (productId is null) { failed = true; messages.Add("No encontré la variante en Inventario."); }
                    else { touchedProducts.Add(productId.Value); messages.Add($"Stock en Inventario: {appStock}"); }
                }

                if (row.NewStatus is { } status && !failed)
                {
                    var error = await _editor.SetStatusAsync(org, row.PublicationId, status, cancellationToken);
                    if (error is null) messages.Add(status == "paused" ? "Pausada" : "Activada");
                    else { failed = true; messages.Add(error); }
                }
            }
            catch (MercadoLibreEditException exception)
            {
                failed = true;
                messages.Add(exception.Message);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Bulk edit row {Line} of import {Import} failed.", row.Line, id);
                failed = true;
                messages.Add("Mercado Libre no respondió.");
            }

            row.Result = failed ? "failed" : "done";
            row.ResultMessage = string.Join(" · ", messages);
            if (failed) import.Failed++;
            else import.Applied++;
            SetRows(import, rows);
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (touchedProducts.Count > 0)
            _stockSync.RequestPush(touchedProducts);
        import.Status = BulkEditStatus.Done;
        import.FinishedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<int?> AdjustInventoryAsync(Guid org, int variantId, int newStock, CancellationToken cancellationToken)
    {
        var variant = await _context.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId && v.OrganizationId == org, cancellationToken);
        if (variant is null)
            return null;
        var delta = newStock - variant.Stock;
        if (delta == 0)
            return variant.ProductId;
        variant.Stock = newStock;
        variant.UpdatedAt = DateTime.UtcNow;
        _context.InventoryMovements.Add(new InventoryMovement
        {
            OrganizationId = org,
            ProductVariantId = variant.Id,
            Type = "ManualAdjustment",
            QuantityDelta = delta,
            StockAfter = newStock,
            Notes = "Cambio masivo por Excel",
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
        return variant.ProductId;
    }

    public static string StatusLabel(string? status) => status switch
    {
        "active" => "activa",
        "paused" => "pausada",
        "closed" => "finalizada",
        "under_review" => "en revisión",
        _ => status ?? string.Empty
    };

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant().Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u') is var s
            ? s.Replace("título", "titulo")
            : string.Empty;

    /// <summary>Acepta 1299, 1,299.50, $1,299 y 1299,50.</summary>
    public static bool TryMoney(string text, out decimal value)
    {
        var clean = text.Replace("$", string.Empty).Replace("MXN", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(" ", string.Empty).Trim();
        if (clean.Contains(',') && !clean.Contains('.') && clean.Length - clean.LastIndexOf(',') - 1 is 1 or 2)
            clean = clean.Replace(',', '.');
        clean = clean.Replace(",", string.Empty);
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
