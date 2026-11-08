using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceSync.Services.Invoicing;

/// <summary>Resultado de timbrar un CFDI.</summary>
public sealed record StampResult(bool Ok, string? Id, string? Uuid, string? Error);

/// <summary>
/// Cliente de Facturama, API Multiemisor (<c>api-lite</c>): el CSD de cada vendedor se sube desde la app y
/// Facturama lo guarda (la app no guarda la llave privada). Credenciales en Render:
/// <c>Facturama__User</c>, <c>Facturama__Password</c> y <c>Facturama__Sandbox=false</c> para producción.
/// </summary>
public sealed class FacturamaClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FacturamaClient> _logger;

    public FacturamaClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<FacturamaClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["Facturama:User"]) && !string.IsNullOrWhiteSpace(_configuration["Facturama:Password"]);

    /// <summary>Pruebas (sandbox) salvo que <c>Facturama:Sandbox</c> sea false.</summary>
    public bool IsSandbox => !string.Equals(_configuration["Facturama:Sandbox"], "false", StringComparison.OrdinalIgnoreCase);

    private string BaseUrl => _configuration["Facturama:BaseUrl"] is { Length: > 0 } b
        ? b.TrimEnd('/')
        : IsSandbox ? "https://apisandbox.facturama.mx" : "https://api.facturama.mx";

    /// <summary>Sube (o reemplaza) el CSD del emisor.</summary>
    public async Task<(bool Ok, string Message)> UploadCsdAsync(string rfc, byte[] certificate, byte[] privateKey, string password, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["Rfc"] = rfc,
            ["Certificate"] = Convert.ToBase64String(certificate),
            ["PrivateKey"] = Convert.ToBase64String(privateKey),
            ["PrivateKeyPassword"] = password
        };
        var (status, content) = await SendAsync(HttpMethod.Post, "/api-lite/csds", body, cancellationToken);
        if (IsSuccess(status))
            return (true, "Facturama recibió tu CSD.");

        // Ya existía: se reemplaza.
        if (status is HttpStatusCode.Conflict or HttpStatusCode.BadRequest && content.Contains("exist", StringComparison.OrdinalIgnoreCase))
        {
            (status, content) = await SendAsync(HttpMethod.Put, "/api-lite/csds/" + Uri.EscapeDataString(rfc), body, cancellationToken);
            if (IsSuccess(status))
                return (true, "Facturama actualizó tu CSD.");
        }
        return (false, Describe(status, content));
    }

    /// <summary>¿Facturama tiene CSD para este RFC?</summary>
    public async Task<bool?> HasCsdAsync(string rfc, CancellationToken cancellationToken)
    {
        var (status, _) = await SendAsync(HttpMethod.Get, "/api-lite/csds/" + Uri.EscapeDataString(rfc), null, cancellationToken);
        if (IsSuccess(status)) return true;
        if (status == HttpStatusCode.NotFound) return false;
        return null;
    }

    public async Task<StampResult> IssueAsync(JsonObject cfdi, CancellationToken cancellationToken)
    {
        var (status, content) = await SendAsync(HttpMethod.Post, "/api-lite/3/cfdis", cfdi, cancellationToken);
        if (!IsSuccess(status))
            return new StampResult(false, null, null, Describe(status, content));
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var id = Str(root, "Id") ?? Str(root, "id");
            string? uuid = null;
            if (root.TryGetProperty("Complement", out var complement) && complement.ValueKind == JsonValueKind.Object &&
                complement.TryGetProperty("TaxStamp", out var stamp) && stamp.ValueKind == JsonValueKind.Object)
                uuid = Str(stamp, "Uuid");
            uuid ??= Str(root, "Uuid") ?? Str(root, "FolioFiscal");
            return new StampResult(id is not null, id, uuid, id is null ? "Facturama no regresó el número del CFDI." : null);
        }
        catch (JsonException)
        {
            return new StampResult(false, null, null, "Facturama regresó una respuesta que no entendí.");
        }
    }

    /// <summary>PDF o XML del CFDI (<paramref name="format"/>: pdf o xml).</summary>
    public async Task<byte[]?> DownloadAsync(string id, string format, CancellationToken cancellationToken)
    {
        var (status, content) = await SendAsync(HttpMethod.Get, $"/api/Cfdi/{format}/issuedLite/{Uri.EscapeDataString(id)}", null, cancellationToken);
        if (!IsSuccess(status))
            return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var base64 = Str(document.RootElement, "Content");
            return base64 is null ? null : Convert.FromBase64String(base64.Replace('-', '+').Replace('_', '/'));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            _logger.LogWarning(exception, "Facturama download {Format} {Id} unreadable.", format, id);
            return null;
        }
    }

    /// <summary>Cancela ante el SAT (motivos 01 a 04).</summary>
    public async Task<(bool Ok, string Message)> CancelAsync(string id, string motive, string? replacementUuid, CancellationToken cancellationToken)
    {
        var query = "?motive=" + Uri.EscapeDataString(motive) +
                    (motive == "01" && !string.IsNullOrWhiteSpace(replacementUuid) ? "&uuidReplacement=" + Uri.EscapeDataString(replacementUuid) : string.Empty);
        var (status, content) = await SendAsync(HttpMethod.Delete, $"/api-lite/cfdis/{Uri.EscapeDataString(id)}{query}", null, cancellationToken);
        if (!IsSuccess(status))
            return (false, Describe(status, content));
        // Facturama responde con el estado de la cancelación (canceled, pending, …).
        var state = TryRead(content, "Status") ?? "canceled";
        return state.Contains("pend", StringComparison.OrdinalIgnoreCase)
            ? (true, "Solicitud enviada al SAT; el comprador tiene que aceptarla (hasta 72 horas).")
            : (true, "Cancelada ante el SAT.");
    }

    private async Task<(HttpStatusCode Status, string Content)> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return (HttpStatusCode.Unauthorized, "{\"Message\":\"missing_credentials\"}");
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_configuration["Facturama:User"]}:{_configuration["Facturama:Password"]}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Headers.UserAgent.ParseAdd("MarketplaceSync/1.0");
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Facturama {Method} {Path} returned {Status}: {Body}", method, path, (int)response.StatusCode, content.Length > 500 ? content[..500] : content);
            return (response.StatusCode, content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Facturama {Method} {Path} failed.", method, path);
            return (HttpStatusCode.ServiceUnavailable, string.Empty);
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    /// <summary>Errores de Facturama/SAT en palabras del vendedor (el detalle del SAT se deja tal cual).</summary>
    public static string Describe(HttpStatusCode status, string content)
    {
        if (status == HttpStatusCode.Unauthorized)
            return content.Contains("missing_credentials")
                ? "Faltan las credenciales de Facturama (Facturama__User y Facturama__Password en Render)."
                : "Facturama rechazó el usuario o la contraseña.";
        if (string.IsNullOrWhiteSpace(content))
            return "Facturama no respondió. Intenta de nuevo en un momento.";

        var messages = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("ModelState", out var modelState) && modelState.ValueKind == JsonValueKind.Object)
                    foreach (var property in modelState.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.Array)
                            messages.AddRange(property.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                if (messages.Count == 0 && Str(root, "Message") is { } message)
                    messages.Add(message);
            }
        }
        catch (JsonException)
        {
            messages.Add(content.Length > 200 ? content[..200] : content);
        }

        var text = string.Join(" · ", messages.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Take(3));
        if (text.Length == 0)
            text = $"Facturama respondió {(int)status}.";
        var lower = text.ToLowerInvariant();
        if (lower.Contains("cfdi40147") || lower.Contains("régimen") && lower.Contains("receptor"))
            text += " (El régimen fiscal del comprador no coincide con el SAT: pídele su Constancia de Situación Fiscal.)";
        else if (lower.Contains("cfdi40145") || lower.Contains("nombre") && lower.Contains("receptor"))
            text += " (El nombre del comprador debe ir exactamente como en su Constancia, sin «S.A. de C.V.».)";
        else if (lower.Contains("cfdi40149") || lower.Contains("domicilio") || lower.Contains("código postal"))
            text += " (El código postal del comprador debe ser el de su domicilio fiscal.)";
        else if (lower.Contains("csd") || lower.Contains("certificado"))
            text += " (Revisa que el CSD esté vigente y subido en Facturación.)";
        else if (lower.Contains("folios") || lower.Contains("timbres"))
            text += " (Se acabaron los folios en Facturama.)";
        return text;
    }

    private static string? TryRead(string content, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return Str(document.RootElement, name);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;
}
