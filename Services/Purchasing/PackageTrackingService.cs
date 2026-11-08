using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Purchasing;

/// <summary>Un cambio de estado que vale la pena avisar.</summary>
public sealed record PackageChange(InboundPackage Package, string OldStatus, string NewStatus);

/// <summary>
/// Paquetes que llegan a la dirección de El Paso. Detecta la paquetería por el número de guía (UPS, USPS,
/// FedEx, Amazon, DHL) y da el link de rastreo. Con <c>Tracking17__ApiKey</c> (17TRACK, API v2.2) consulta el
/// estado solo y avisa cuando sale a entrega o se entrega; sin llave se marca a mano.
/// </summary>
public sealed class PackageTrackingService
{
    public const int BatchSize = 40;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PackageTrackingService> _logger;

    public PackageTrackingService(AppDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<PackageTrackingService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["Tracking17:ApiKey"]);
    private string BaseUrl => _configuration["Tracking17:BaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.17track.net/track/v2.2";

    // ─────────────────────────────────────────────────────────────
    // Paqueterías
    // ─────────────────────────────────────────────────────────────

    public static string Normalize(string? number) =>
        Regex.Replace(number ?? string.Empty, @"[\s-]", string.Empty).ToUpperInvariant();

    public static string DetectCarrier(string number) => number switch
    {
        _ when Regex.IsMatch(number, @"^1Z[0-9A-Z]{16}$") => "ups",
        _ when Regex.IsMatch(number, @"^TBA\d{9,15}$") => "amazon",
        _ when Regex.IsMatch(number, @"^(9[1-5]\d{18,24}|[A-Z]{2}\d{9}US|420\d{5,9}9\d{21,25})$") => "usps",
        _ when Regex.IsMatch(number, @"^(\d{12}|\d{15}|\d{20}|96\d{20})$") => "fedex",
        _ when Regex.IsMatch(number, @"^(\d{10}|JD\d{18}|JJD\d{16,18})$") => "dhl",
        _ => "other"
    };

    public static string CarrierLabel(string carrier) => carrier switch
    {
        "ups" => "UPS",
        "usps" => "USPS",
        "fedex" => "FedEx",
        "amazon" => "Amazon",
        "dhl" => "DHL",
        _ => "Paquetería"
    };

    public static string TrackingUrl(string carrier, string number) => carrier switch
    {
        "ups" => $"https://www.ups.com/track?tracknum={Uri.EscapeDataString(number)}",
        "usps" => $"https://tools.usps.com/go/TrackConfirmAction?tLabels={Uri.EscapeDataString(number)}",
        "fedex" => $"https://www.fedex.com/fedextrack/?trknbr={Uri.EscapeDataString(number)}",
        "amazon" => $"https://track.amazon.com/tracking/{Uri.EscapeDataString(number)}",
        "dhl" => $"https://www.dhl.com/us-en/home/tracking.html?tracking-id={Uri.EscapeDataString(number)}",
        _ => $"https://t.17track.net/es#nums={Uri.EscapeDataString(number)}"
    };

    public static string StatusLabel(string status) => status switch
    {
        "InfoReceived" => "Guía creada",
        "InTransit" => "En camino",
        "OutForDelivery" => "Sale a entrega hoy",
        "AvailableForPickup" => "Lista para recoger",
        "Delivered" => "Entregado",
        "DeliveryFailure" => "No pudieron entregar",
        "Exception" => "Problema con el envío",
        "Expired" => "Sin movimiento",
        _ => "Sin información"
    };

    public static bool IsAlert(string status) => status is "OutForDelivery" or "Delivered" or "AvailableForPickup" or "DeliveryFailure" or "Exception";

    // ─────────────────────────────────────────────────────────────
    // Alta y cambios
    // ─────────────────────────────────────────────────────────────

    public async Task<(InboundPackage? Package, string Message)> AddAsync(Guid org, string? number, string? description, int? tripId, CancellationToken cancellationToken = default)
    {
        var clean = Normalize(number);
        if (clean.Length is < 8 or > 40 || !Regex.IsMatch(clean, "^[A-Z0-9]+$"))
            return (null, "Ese número de guía no se ve bien.");
        var existing = await _context.InboundPackages.FirstOrDefaultAsync(p => p.OrganizationId == org && p.TrackingNumber == clean, cancellationToken);
        if (existing is not null)
        {
            if (existing.ArchivedAt is not null) existing.ArchivedAt = null;
            if (!string.IsNullOrWhiteSpace(description)) existing.Description = Short(description, 200);
            if (tripId is not null) existing.SupplyTripId = tripId;
            await _context.SaveChangesAsync(cancellationToken);
            return (existing, "Esa guía ya estaba; la actualicé.");
        }
        if (tripId is { } t && !await _context.SupplyTrips.AnyAsync(x => x.Id == t && x.OrganizationId == org, cancellationToken))
            tripId = null;

        var package = new InboundPackage
        {
            OrganizationId = org,
            TrackingNumber = clean,
            Carrier = DetectCarrier(clean),
            Description = string.IsNullOrWhiteSpace(description) ? null : Short(description, 200),
            SupplyTripId = tripId
        };
        _context.InboundPackages.Add(package);
        await _context.SaveChangesAsync(cancellationToken);
        if (IsConfigured)
            await RefreshAsync(org, new[] { package.Id }, cancellationToken);
        return (package, $"Guía {CarrierLabel(package.Carrier)} agregada.");
    }

    public async Task<bool> MarkDeliveredAsync(Guid org, int id, CancellationToken cancellationToken = default)
    {
        var package = await _context.InboundPackages.FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == org, cancellationToken);
        if (package is null) return false;
        package.Status = "Delivered";
        package.DeliveredAt ??= DateTime.UtcNow;
        package.NotifiedStatus = "Delivered";
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ArchiveAsync(Guid org, int id, CancellationToken cancellationToken = default)
    {
        var package = await _context.InboundPackages.FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == org, cancellationToken);
        if (package is null) return false;
        package.ArchivedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // 17TRACK
    // ─────────────────────────────────────────────────────────────

    /// <summary>Consulta los paquetes sin entregar (o los indicados). Regresa los que cambiaron de estado.</summary>
    public async Task<List<PackageChange>> RefreshAsync(Guid org, IReadOnlyCollection<int>? ids = null, CancellationToken cancellationToken = default)
    {
        var changes = new List<PackageChange>();
        if (!IsConfigured)
            return changes;
        var query = _context.InboundPackages.Where(p => p.OrganizationId == org && p.ArchivedAt == null);
        query = ids is null ? query.Where(p => p.Status != "Delivered" && p.CreatedAt >= DateTime.UtcNow.AddDays(-60)) : query.Where(p => ids.Contains(p.Id));
        var packages = await query.OrderBy(p => p.LastCheckedAt ?? DateTime.MinValue).Take(BatchSize * 3).ToListAsync(cancellationToken);
        if (packages.Count == 0)
            return changes;

        // Alta en 17TRACK (una vez por guía).
        foreach (var chunk in packages.Where(p => !p.Registered).Chunk(BatchSize))
        {
            var body = new JsonArray(chunk.Select(p => (JsonNode)new JsonObject { ["number"] = p.TrackingNumber }).ToArray());
            var (ok, content) = await PostAsync("/register", body, cancellationToken);
            if (!ok) continue;
            var accepted = Numbers(content, "accepted");
            // "Ya registrado" también cuenta como alta.
            var rejected = RejectedAlready(content);
            foreach (var p in chunk.Where(p => accepted.Contains(p.TrackingNumber) || rejected.Contains(p.TrackingNumber)))
                p.Registered = true;
        }

        foreach (var chunk in packages.Where(p => p.Registered).Chunk(BatchSize))
        {
            var body = new JsonArray(chunk.Select(p => (JsonNode)new JsonObject { ["number"] = p.TrackingNumber }).ToArray());
            var (ok, content) = await PostAsync("/gettrackinfo", body, cancellationToken);
            if (!ok) continue;
            try
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("accepted", out var list)) continue;
                foreach (var entry in list.EnumerateArray())
                {
                    var number = Str(entry, "number");
                    var package = chunk.FirstOrDefault(p => p.TrackingNumber == number);
                    if (package is null || !entry.TryGetProperty("track_info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                    var old = package.Status;
                    if (info.TryGetProperty("latest_status", out var latest))
                        package.Status = Str(latest, "status") ?? package.Status;
                    if (info.TryGetProperty("latest_event", out var ev) && ev.ValueKind == JsonValueKind.Object)
                    {
                        package.LastEvent = Short(Str(ev, "description"), 300);
                        package.Location = Short(Str(ev, "location"), 120);
                        package.LastEventAt = Date(Str(ev, "time_utc") ?? Str(ev, "time_iso"));
                    }
                    if (info.TryGetProperty("time_metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object &&
                        metrics.TryGetProperty("estimated_delivery_date", out var eta) && eta.ValueKind == JsonValueKind.Object &&
                        Date(Str(eta, "to") ?? Str(eta, "from")) is { } etaDate)
                        package.EstimatedDelivery = DateOnly.FromDateTime(etaDate.AddHours(-7));
                    if (package.Status == "Delivered")
                        package.DeliveredAt ??= package.LastEventAt ?? DateTime.UtcNow;
                    if (old != package.Status)
                        changes.Add(new PackageChange(package, old, package.Status));
                }
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "17TRACK response unreadable.");
            }
            foreach (var p in chunk) p.LastCheckedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return changes;
    }

    private async Task<(bool Ok, string Content)> PostAsync(string path, JsonNode body, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("17token", _configuration["Tracking17:ApiKey"]);
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("17TRACK {Path} returned {Status}: {Body}", path, (int)response.StatusCode, content.Length > 300 ? content[..300] : content);
            return (response.IsSuccessStatusCode, content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "17TRACK {Path} failed.", path);
            return (false, string.Empty);
        }
    }

    private static HashSet<string> Numbers(string content, string listName)
    {
        var set = new HashSet<string>();
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty(listName, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                    if (Str(e, "number") is { } n) set.Add(n);
        }
        catch (JsonException) { }
        return set;
    }

    private static HashSet<string> RejectedAlready(string content)
    {
        var set = new HashSet<string>();
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("rejected", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                    if (Str(e, "number") is { } n && e.TryGetProperty("error", out var err) &&
                        (Str(err, "message") ?? string.Empty).Contains("register", StringComparison.OrdinalIgnoreCase))
                        set.Add(n);
        }
        catch (JsonException) { }
        return set;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static DateTime? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    private static string Short(string? text, int max) => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text.Trim() : text[..max].Trim();
}
