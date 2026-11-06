using System.IO.Compression;
using System.Net.Http.Headers;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Orders;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Shipping;

public sealed record LabelFile(byte[] Content, string ContentType, string FileName, int Labels, IReadOnlyList<string> Skipped);

/// <summary>
/// Etiquetas de Mercado Envíos en lote: GET /shipment_labels?shipment_ids=…&amp;response_type=pdf|zpl2 (hasta 50 por llamada).
/// Un archivo si cabe en una llamada; si no, un .zip con un archivo por lote.
/// </summary>
public sealed class ShippingLabelService
{
    private const int BatchSize = 50;
    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShippingLabelService> _logger;

    public ShippingLabelService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<ShippingLabelService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";

    public async Task<(LabelFile? File, string? Error)> GetLabelsAsync(Guid org, IReadOnlyList<int> orderIds, bool thermal, CancellationToken cancellationToken = default)
    {
        var orders = await _context.SalesOrders
            .Where(o => o.OrganizationId == org && orderIds.Contains(o.Id))
            .ToListAsync(cancellationToken);
        var skipped = new List<string>();
        var printable = new List<SalesOrder>();
        foreach (var o in orders)
        {
            if (string.IsNullOrWhiteSpace(o.ShippingId)) skipped.Add($"#{o.ExternalOrderId}: no usa Mercado Envíos");
            else if (o.ShippingLogistic == "fulfillment") skipped.Add($"#{o.ExternalOrderId}: es Full, lo envía Mercado Libre");
            else if (o.ShippingStatus != "ready_to_ship") skipped.Add($"#{o.ExternalOrderId}: la etiqueta aún no está lista ({o.ShippingStatus ?? "sin dato"})");
            else printable.Add(o);
        }
        if (printable.Count == 0)
            return (null, skipped.Count == 0 ? "Elige al menos un pedido por enviar." : "Ninguna etiqueta está lista: " + string.Join("; ", skipped.Take(5)));

        var type = thermal ? "zpl2" : "pdf";
        var ext = thermal ? "zpl" : "pdf";
        var files = new List<(string Name, byte[] Bytes)>();
        // Un shipment se repite si varios pedidos van en el mismo paquete (carrito).
        foreach (var group in printable.GroupBy(o => o.MarketplaceAccountId))
        {
            var token = await _auth.GetValidAccessTokenAsync(org, group.Key);
            foreach (var batch in group.Select(o => o.ShippingId!).Distinct().Chunk(BatchSize))
            {
                var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiBase}/shipment_labels?shipment_ids={string.Join(',', batch.Select(Uri.EscapeDataString))}&response_type={type}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var text = System.Text.Encoding.UTF8.GetString(bytes);
                    _logger.LogInformation("Labels rejected: {Body}", text);
                    skipped.Add($"Mercado Libre no entregó {batch.Length} etiqueta(s): {MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DescribeError(text)}");
                    continue;
                }
                // ZPL puede venir dentro de un zip: se guarda tal cual con su extensión.
                var isZip = bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K';
                files.Add(($"etiquetas-{files.Count + 1}.{(isZip ? "zip" : ext)}", bytes));
            }
        }
        if (files.Count == 0)
            return (null, string.Join(" ", skipped));

        var now = DateTime.UtcNow;
        foreach (var o in printable) o.LabelPrintedAt = now;
        await _context.SaveChangesAsync(cancellationToken);

        var stamp = DateTime.UtcNow.AddHours(-7).ToString("yyyyMMdd-HHmm");
        if (files.Count == 1)
        {
            var (name, bytes) = files[0];
            var contentType = name.EndsWith(".pdf") ? "application/pdf" : name.EndsWith(".zip") ? "application/zip" : "text/plain";
            return (new LabelFile(bytes, contentType, $"etiquetas-{stamp}{Path.GetExtension(name)}", printable.Count, skipped), null);
        }

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes, cancellationToken);
            }
        }
        return (new LabelFile(zip.ToArray(), "application/zip", $"etiquetas-{stamp}.zip", printable.Count, skipped), null);
    }
}
