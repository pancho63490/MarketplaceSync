using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Photos;

public sealed record PhotoState(string Label, string Tone, string Help, bool Fixable);

public sealed record PhotoFixResult(string? Message, string? Error);

/// <summary>
/// Fotos con fondo blanco para Mercado Libre (la portada debe ser fondo blanco liso, producto centrado y grande, 1200×1200).
/// Revisa la portada (borde blanco, liso o con cosas; qué tanto ocupa el producto; resolución) y la arregla:
/// fondo liso → se cambia por blanco con ImageMagick (relleno desde las orillas); fondo con cosas → remove.bg si hay
/// clave (<c>RemoveBg:ApiKey</c>). Siempre recorta, centra y deja 1200×1200 JPG. La foto nueva se sube a Mercado Libre
/// (<c>POST /pictures/items/upload</c>) y, si se pide, reemplaza la portada de las publicaciones activas.
/// </summary>
public sealed class PhotoStudioService
{
    public const int Canvas = 1200;
    public const int MinSide = 500;
    private const int Sample = 64;

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PhotoStudioService> _logger;

    private static string? _magick;

    public PhotoStudioService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<PhotoStudioService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";
    private string RemoveBgBase => _configuration["RemoveBg:BaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.remove.bg";
    private string? RemoveBgKey => _configuration["RemoveBg:ApiKey"] is { Length: > 0 } k ? k : null;

    public bool AiConfigured => RemoveBgKey is not null;
    public bool ToolAvailable => Magick() is not null;

    public static PhotoState Describe(string? check) => check switch
    {
        "ok" => new("Lista", "pill-ok", "Fondo blanco, producto grande y centrado.", false),
        "fill" => new("Centrar y agrandar", "pill-warn", "El fondo es blanco pero el producto se ve chico o la foto no es cuadrada.", true),
        "plain" => new("Fondo liso de color", "pill-warn", "El fondo es de un solo color: se cambia por blanco.", true),
        "busy" => new("Fondo con cosas", "pill-bad", "Hay objetos o colores en el fondo: hace falta quitarlo con IA o tomar otra foto.", true),
        "small" => new("Foto muy chica", "pill-bad", $"Mide menos de {MinSide}×{MinSide}: se puede agrandar pero se verá borrosa. Mejor busca otra foto.", true),
        "error" => new("No se pudo abrir", "pill-bad", "La tienda no dejó descargar la foto. Cambia el link de la foto.", false),
        _ => new("Sin revisar", "pill", "Todavía no se revisa.", false)
    };

    // ─────────────────────────────────────────────────────────────
    // Revisar
    // ─────────────────────────────────────────────────────────────

    public async Task<string> CheckAsync(Product product, CancellationToken cancellationToken = default)
    {
        string check;
        try
        {
            var bytes = await DownloadAsync(product.ImageUrl!, cancellationToken);
            var (w, h, rgb) = await AnalyzeInputAsync(bytes, cancellationToken);
            product.PhotoWidth = w;
            product.PhotoHeight = h;
            check = Classify(w, h, rgb);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not InvalidOperationException { Message: "magick" })
        {
            _logger.LogInformation(exception, "Photo check failed for product {Product}.", product.Id);
            check = "error";
        }
        product.PhotoCheck = check;
        product.PhotoCheckedAt = DateTime.UtcNow;
        return check;
    }

    /// <summary>Revisa las portadas que nunca se revisaron o cambiaron. Devuelve cuántas revisó.</summary>
    public async Task<int> CheckPendingAsync(Guid org, int max = 80, CancellationToken cancellationToken = default)
    {
        if (!ToolAvailable)
            return 0;
        var products = await _context.Products
            .Where(p => p.OrganizationId == org && p.ImageUrl != null && p.ImageUrl != "" && p.PhotoCheckedAt == null)
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Take(max)
            .ToListAsync(cancellationToken);
        foreach (var product in products)
        {
            await CheckAsync(product, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }
        return products.Count;
    }

    // ─────────────────────────────────────────────────────────────
    // Arreglar
    // ─────────────────────────────────────────────────────────────

    public async Task<PhotoFixResult> FixAsync(Guid org, int productId, bool updateListings, CancellationToken cancellationToken = default)
    {
        var product = await _context.Products.Include(p => p.MarketplacePublications)
            .FirstOrDefaultAsync(p => p.Id == productId && p.OrganizationId == org, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (string.IsNullOrWhiteSpace(product.ImageUrl))
            return new(null, "Este producto no tiene foto.");
        if (!ToolAvailable)
            return new(null, "El servidor no tiene ImageMagick instalado. Vuelve a desplegar en Render (el Dockerfile ya lo instala).");

        var accountId = await AccountForAsync(org, product, cancellationToken);
        if (accountId is null)
            return new(null, "Conecta tu cuenta de Mercado Libre: ahí se guarda la foto nueva.");

        byte[] source;
        try
        {
            source = await DownloadAsync(product.ImageUrl, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Photo download failed for product {Product}.", product.Id);
            product.PhotoCheck = "error";
            product.PhotoCheckedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return new(null, "La tienda no dejó descargar la foto. Cambia el link de la foto en el producto.");
        }

        var (w, h, rgb) = await AnalyzeInputAsync(source, cancellationToken);
        var check = Classify(w, h, rgb);
        if (check == "ok")
        {
            product.PhotoCheck = "ok";
            product.PhotoCheckedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return new("La foto ya estaba bien: fondo blanco y producto centrado.", null);
        }

        byte[] result;
        var usedAi = false;
        var background = BorderBackground(rgb);
        if (check == "busy" || (check == "small" && background is null))
        {
            if (RemoveBgKey is null)
                return new(null, "El fondo tiene cosas y no se puede limpiar solo. Agrega en Render la clave RemoveBg__ApiKey (remove.bg) o cambia la foto por una con fondo liso.");
            var (cut, error) = await RemoveBackgroundAsync(source, cancellationToken);
            if (cut is null)
                return new(null, error);
            result = await RenderAsync(cut, null, cancellationToken);
            usedAi = true;
        }
        else
        {
            result = await RenderAsync(source, background, cancellationToken);
        }

        var (url, pictureId, uploadError) = await UploadAsync(org, accountId.Value, result, cancellationToken);
        if (url is null)
            return new(null, uploadError);

        product.OriginalImageUrl ??= product.ImageUrl;
        product.ImageUrl = url;
        product.PhotoCheck = "ok";
        product.PhotoWidth = Canvas;
        product.PhotoHeight = Canvas;
        product.PhotoCheckedAt = DateTime.UtcNow;
        product.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var message = usedAi ? "Listo: se quitó el fondo con IA y quedó en blanco, centrada y en 1200×1200."
            : check == "fill" ? "Listo: producto centrado y más grande, en 1200×1200."
            : "Listo: fondo blanco, producto centrado, 1200×1200.";
        if (check == "small")
            message += " Ojo: la original era chica y puede verse borrosa.";
        if (updateListings)
            message += await ReplaceCoverAsync(org, product, new { id = pictureId }, cancellationToken);
        return new(message, null);
    }

    public async Task<PhotoFixResult> UndoAsync(Guid org, int productId, bool updateListings, CancellationToken cancellationToken = default)
    {
        var product = await _context.Products.Include(p => p.MarketplacePublications)
            .FirstOrDefaultAsync(p => p.Id == productId && p.OrganizationId == org, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (string.IsNullOrWhiteSpace(product.OriginalImageUrl))
            return new(null, "No hay una foto original guardada.");
        product.ImageUrl = product.OriginalImageUrl;
        product.OriginalImageUrl = null;
        product.PhotoCheck = null;
        product.PhotoCheckedAt = null;
        product.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        var message = "Regresó la foto original.";
        if (updateListings)
            message += await ReplaceCoverAsync(org, product, new { source = product.ImageUrl }, cancellationToken);
        return new(message, null);
    }

    /// <summary>Arregla las que se pueden arreglar solas (y las de fondo con cosas si hay remove.bg).</summary>
    public async Task<(int Fixed, int Failed)> FixAllAsync(Guid org, bool updateListings, int max = 40, CancellationToken cancellationToken = default)
    {
        await CheckPendingAsync(org, 120, cancellationToken);
        var fixable = AiConfigured ? new[] { "fill", "plain", "busy", "small" } : new[] { "fill", "plain" };
        var ids = await _context.Products.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.PhotoCheck != null && fixable.Contains(p.PhotoCheck))
            .OrderByDescending(p => p.MarketplacePublications.Any(m => m.IsPublished))
            .ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Select(p => p.Id).Take(max).ToListAsync(cancellationToken);
        int ok = 0, failed = 0;
        foreach (var id in ids)
        {
            try
            {
                var result = await FixAsync(org, id, updateListings, cancellationToken);
                if (result.Error is null) ok++; else failed++;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Photo fix failed for product {Product}.", id);
                failed++;
            }
            _context.ChangeTracker.Clear();
        }
        return (ok, failed);
    }

    // ─────────────────────────────────────────────────────────────
    // Análisis
    // ─────────────────────────────────────────────────────────────

    private static bool IsWhite(byte r, byte g, byte b) => r >= 236 && g >= 236 && b >= 236 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 14;

    private static IEnumerable<int> BorderIndexes()
    {
        for (var x = 0; x < Sample; x++) { yield return x; yield return (Sample - 1) * Sample + x; }
        for (var y = 1; y < Sample - 1; y++) { yield return y * Sample; yield return y * Sample + Sample - 1; }
    }

    private static double Dist(byte[] rgb, int i, (double R, double G, double B) c) =>
        Math.Sqrt(Math.Pow(rgb[i * 3] - c.R, 2) + Math.Pow(rgb[i * 3 + 1] - c.G, 2) + Math.Pow(rgb[i * 3 + 2] - c.B, 2));

    /// <summary>Color del fondo si las orillas son de un solo color (o blancas); null si tienen cosas.</summary>
    private static (double R, double G, double B)? BorderBackground(byte[] rgb)
    {
        var border = BorderIndexes().ToList();
        var mean = (border.Average(i => (double)rgb[i * 3]), border.Average(i => (double)rgb[i * 3 + 1]), border.Average(i => (double)rgb[i * 3 + 2]));
        var near = border.Count(i => Dist(rgb, i, mean) < 34) / (double)border.Count;
        return near >= 0.85 ? mean : null;
    }

    public static string Classify(int width, int height, byte[] rgb)
    {
        var border = BorderIndexes().ToList();
        var white = border.Count(i => IsWhite(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2])) / (double)border.Count;
        var bg = BorderBackground(rgb);

        // Qué tanto ocupa el producto: caja de los píxeles que no son fondo.
        double fill = 0;
        if (bg is { } c)
        {
            int minX = Sample, minY = Sample, maxX = -1, maxY = -1;
            for (var y = 0; y < Sample; y++)
            for (var x = 0; x < Sample; x++)
            {
                if (Dist(rgb, y * Sample + x, c) <= 40) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (maxX >= 0)
            {
                // La muestra es 64×64 sin respetar proporción: se regresa a la proporción real para medir el lado mayor.
                var bw = (maxX - minX + 1) / (double)Sample * width;
                var bh = (maxY - minY + 1) / (double)Sample * height;
                fill = Math.Max(bw, bh) / Math.Max(width, height);
            }
        }

        if (Math.Min(width, height) < MinSide)
            return "small";
        if (white >= 0.9)
        {
            var square = Math.Abs(width - height) / (double)Math.Max(width, height) <= 0.08;
            return fill >= 0.8 && square ? "ok" : "fill";
        }
        return bg is null ? "busy" : "plain";
    }

    private async Task<(int W, int H, byte[] Rgb)> AnalyzeInputAsync(byte[] image, CancellationToken cancellationToken)
    {
        var input = await TempAsync(image, cancellationToken);
        try
        {
            var (code, info, err) = await RunAsync(new[] { input + "[0]", "-format", "%w %h", "info:" }, cancellationToken);
            var parts = System.Text.Encoding.ASCII.GetString(info).Trim().Split(' ');
            if (code != 0 || parts.Length < 2)
                throw new InvalidDataException("No es una imagen: " + err);
            var (code2, rgb, err2) = await RunAsync(new[]
            {
                input + "[0]", "-auto-orient", "-background", "white", "-alpha", "remove", "-alpha", "off",
                "-resize", $"{Sample}x{Sample}!", "-depth", "8", "rgb:-"
            }, cancellationToken);
            if (code2 != 0 || rgb.Length < Sample * Sample * 3)
                throw new InvalidDataException("No pude leer la imagen: " + err2);
            return (int.Parse(parts[0]), int.Parse(parts[1]), rgb);
        }
        finally
        {
            TryDelete(input);
        }
    }

    /// <summary>
    /// Fondo blanco + recorte + centrado en 1200×1200. Con <paramref name="background"/> rellena desde las orillas ese color
    /// (con tolerancia) y lo vuelve blanco; sin él, la imagen ya trae el fondo limpio (remove.bg o fondo blanco).
    /// </summary>
    private async Task<byte[]> RenderAsync(byte[] image, (double R, double G, double B)? background, CancellationToken cancellationToken)
    {
        var input = await TempAsync(image, cancellationToken);
        var output = Path.Combine(Path.GetTempPath(), $"ms-photo-{Guid.NewGuid():N}.jpg");
        try
        {
            var args = new List<string> { input + "[0]", "-auto-orient", "-background", "white", "-alpha", "remove", "-alpha", "off" };
            if (background is { } c && !(c.R >= 236 && c.G >= 236 && c.B >= 236))
            {
                var color = $"rgb({(int)c.R},{(int)c.G},{(int)c.B})";
                args.AddRange(new[]
                {
                    "-bordercolor", color, "-border", "3", "-alpha", "set", "-fuzz", "14%", "-fill", "none",
                    "-floodfill", "+0+0", color, "-shave", "3x3", "-background", "white", "-alpha", "remove", "-alpha", "off"
                });
            }
            var inner = (int)(Canvas * 0.92);
            args.AddRange(new[]
            {
                "-fuzz", "7%", "-trim", "+repage",
                "-resize", $"{inner}x{inner}",
                "-background", "white", "-gravity", "center", "-extent", $"{Canvas}x{Canvas}",
                "-strip", "-quality", "92", "jpg:" + output
            });
            var (code, _, err) = await RunAsync(args, cancellationToken);
            if (code != 0 || !File.Exists(output))
                throw new InvalidOperationException("ImageMagick falló: " + err);
            return await File.ReadAllBytesAsync(output, cancellationToken);
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Servicios externos
    // ─────────────────────────────────────────────────────────────

    private async Task<(byte[]? Image, string? Error)> RemoveBackgroundAsync(byte[] image, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "image_file", "foto");
        form.Add(new StringContent("auto"), "size");
        form.Add(new StringContent("ffffff"), "bg_color");
        form.Add(new StringContent("jpg"), "format");
        using var request = new HttpRequestMessage(HttpMethod.Post, RemoveBgBase + "/v1.0/removebg") { Content = form };
        request.Headers.Add("X-Api-Key", RemoveBgKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
            return (bytes, null);
        var detail = System.Text.Encoding.UTF8.GetString(bytes);
        _logger.LogInformation("remove.bg rejected ({Status}): {Body}", (int)response.StatusCode, detail[..Math.Min(300, detail.Length)]);
        return (null, (int)response.StatusCode switch
        {
            402 => "remove.bg dice que ya no tienes créditos. Recarga en remove.bg o arregla la foto a mano.",
            401 or 403 => "La clave de remove.bg no es válida. Revísala en Render (RemoveBg__ApiKey).",
            429 => "remove.bg está saturado; intenta en un minuto.",
            _ => "remove.bg no pudo quitar el fondo de esta foto (" + (int)response.StatusCode + ")."
        });
    }

    private async Task<(string? Url, string? Id, string? Error)> UploadAsync(Guid org, int accountId, byte[] jpg, CancellationToken cancellationToken)
    {
        var token = await _auth.GetValidAccessTokenAsync(org, accountId);
        var client = _httpClientFactory.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(jpg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "portada.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/pictures/items/upload") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Picture upload rejected: {Body}", content);
            return (null, null, "Mercado Libre no aceptó la foto: " + MarketplaceSync.Services.MercadoLibre.MercadoLibreListingClient.DescribeError(content));
        }
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var i) ? i.ToString() : null;
        var max = root.TryGetProperty("max_size", out var m) ? m.GetString() : null;
        string? url = null;
        if (root.TryGetProperty("variations", out var vars) && vars.ValueKind == JsonValueKind.Array)
        {
            var list = vars.EnumerateArray().ToList();
            var best = list.FirstOrDefault(v => v.TryGetProperty("size", out var s) && s.GetString() == max);
            if (best.ValueKind != JsonValueKind.Object && list.Count > 0) best = list[0];
            if (best.ValueKind == JsonValueKind.Object)
                url = (best.TryGetProperty("secure_url", out var su) ? su.GetString() : null) ?? (best.TryGetProperty("url", out var u) ? u.GetString() : null);
        }
        return url is null || id is null ? (null, null, "Mercado Libre no regresó el link de la foto.") : (url, id, null);
    }

    /// <summary>Pone la foto como portada en las publicaciones activas o pausadas del producto (conserva las demás).</summary>
    private async Task<string> ReplaceCoverAsync(Guid org, Product product, object cover, CancellationToken cancellationToken)
    {
        var listings = product.MarketplacePublications
            .Where(p => p.Marketplace == "MercadoLibre" && !string.IsNullOrWhiteSpace(p.ExternalItemId) && p.MarketplaceAccountId.HasValue
                        && (p.Status is "active" or "paused" || p.IsPublished))
            .ToList();
        if (listings.Count == 0)
            return string.Empty;
        var done = 0;
        foreach (var listing in listings)
        {
            try
            {
                var token = await _auth.GetValidAccessTokenAsync(org, listing.MarketplaceAccountId!.Value);
                var client = _httpClientFactory.CreateClient();
                var item = Uri.EscapeDataString(listing.ExternalItemId!);
                using var get = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/items/{item}?attributes=pictures");
                get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var current = await client.SendAsync(get, cancellationToken);
                var ids = new List<string>();
                if (current.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await current.Content.ReadAsStringAsync(cancellationToken));
                    if (doc.RootElement.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
                        ids.AddRange(pics.EnumerateArray().Select(p => p.TryGetProperty("id", out var pid) ? pid.GetString() : null).OfType<string>());
                }
                var pictures = new List<object> { cover };
                pictures.AddRange(ids.Skip(1).Take(9).Select(id => (object)new { id }));
                using var put = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/items/{item}")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { pictures }), System.Text.Encoding.UTF8, "application/json")
                };
                put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(put, cancellationToken);
                if (response.IsSuccessStatusCode) done++;
                else _logger.LogInformation("Cover replace rejected for {Item}: {Body}", listing.ExternalItemId, await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Cover replace failed for {Item}.", listing.ExternalItemId);
            }
        }
        return done == listings.Count
            ? $" También se cambió en {(done == 1 ? "tu publicación" : $"tus {done} publicaciones")} de Mercado Libre."
            : $" En Mercado Libre se cambió en {done} de {listings.Count} publicaciones; revisa las demás en Editar.";
    }

    private async Task<int?> AccountForAsync(Guid org, Product product, CancellationToken cancellationToken)
    {
        var fromListing = product.MarketplacePublications.FirstOrDefault(p => p.MarketplaceAccountId.HasValue)?.MarketplaceAccountId;
        if (fromListing is not null)
            return fromListing;
        return await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .OrderBy(a => a.Id).Select(a => (int?)a.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("Link de foto inválido.");
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MarketplaceSync/1.0)");
        request.Headers.Accept.ParseAdd("image/*");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 20_000_000)
            throw new InvalidDataException("Foto demasiado grande.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 20_000_000)
            throw new InvalidDataException("Foto demasiado grande.");
        return bytes;
    }

    // ─────────────────────────────────────────────────────────────
    // ImageMagick
    // ─────────────────────────────────────────────────────────────

    private static string? Magick()
    {
        if (_magick is not null)
            return _magick.Length == 0 ? null : _magick;
        foreach (var candidate in new[] { "magick", "convert" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(candidate, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                if (p is null) continue;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode == 0 && output.Contains("ImageMagick")) { _magick = candidate; return candidate; }
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        _magick = string.Empty;
        return null;
    }

    private static async Task<(int Code, byte[] Stdout, string Stderr)> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var exe = Magick() ?? throw new InvalidOperationException("magick");
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Límites para que una foto enorme no tumbe el servidor.
        foreach (var a in new[] { "-limit", "memory", "256MiB", "-limit", "map", "512MiB", "-limit", "time", "60" }) info.ArgumentList.Add(a);
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("magick");
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        await copy;
        return (process.ExitCode, stdout.ToArray(), await stderr);
    }

    private static async Task<string> TempAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-photo-{Guid.NewGuid():N}.img");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
