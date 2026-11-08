using Hangfire;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Automation;

/// <summary>Algo que no está funcionando y qué hacer. <c>Key</c> identifica el problema para avisar una vez al día.</summary>
public sealed record HealthProblem(string Key, string Title, string Detail, string? FixPath);

/// <summary>Una revisión que salió bien (para /estado).</summary>
public sealed record HealthCheck(string Name, bool Ok, string Detail);

/// <summary>
/// Revisa que lo automático esté funcionando: sesión de cada cuenta de Mercado Libre, que los pedidos se
/// sincronicen, trabajos en segundo plano que fallaron, stock que Mercado Libre no aceptó, facturas que no se
/// subieron y llaves que faltan (IA, Facturama). Lo usa el aviso de fallas por Telegram y /estado.
/// </summary>
public sealed class HealthMonitorService
{
    private static readonly TimeSpan OrdersSyncLimit = TimeSpan.FromHours(3);

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IServiceProvider _services;
    private readonly ILogger<HealthMonitorService> _logger;

    public HealthMonitorService(AppDbContext context, IMercadoLibreAuthService auth, IServiceProvider services, ILogger<HealthMonitorService> logger)
    {
        _context = context;
        _auth = auth;
        _services = services;
        _logger = logger;
    }

    public async Task<(List<HealthProblem> Problems, List<HealthCheck> Checks)> CheckAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var problems = new List<HealthProblem>();
        var checks = new List<HealthCheck>();
        var now = DateTime.UtcNow;

        // 1) Cuentas de Mercado Libre: sesión y sincronización de pedidos.
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            problems.Add(new("ml-none", "No hay cuenta de Mercado Libre conectada", "Sin cuenta no llegan pedidos ni preguntas.", "/MercadoLibre/Status"));
        }
        foreach (var account in accounts)
        {
            var name = account.DisplayName ?? account.ExternalAccountId;
            try
            {
                await _auth.GetValidAccessTokenAsync(org, account.Id);
                checks.Add(new($"Sesión de Mercado Libre ({name})", true, "Conectada"));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Health: token for account {Account} failed.", account.Id);
                problems.Add(new($"ml-token:{account.Id}", $"Se cerró la sesión de Mercado Libre ({name})",
                    "Mercado Libre no aceptó la sesión: vuelve a conectar la cuenta. Mientras, no llegan pedidos, preguntas ni avisos.", "/MercadoLibre/Status"));
                continue;
            }

            if (account.LastOrdersSyncError is { Length: > 0 } syncError)
                problems.Add(new($"ml-orders-error:{account.Id}", $"Los pedidos de {name} no se están sincronizando", Short(syncError, 200), "/Orders"));
            else if (account.LastOrdersSyncAt is { } last && now - last > OrdersSyncLimit)
                problems.Add(new($"ml-orders-stale:{account.Id}", $"Los pedidos de {name} no se actualizan desde hace {Hours(now - last)}",
                    "Puede que el servidor se haya dormido (plan gratis de Render) o que Mercado Libre no responda.", "/Orders"));
            else
                checks.Add(new($"Pedidos ({name})", true, account.LastOrdersSyncAt is { } at ? $"Actualizados hace {Minutes(now - at)}" : "Pendiente la primera sincronización"));
        }

        // 2) Stock que Mercado Libre rechazó en las últimas 24 horas.
        var since = now.AddHours(-24);
        var stockErrors = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.LastStockPushError != null && p.LastStockPushAt >= since)
            .Select(p => new { p.Product.Title, p.LastStockPushError })
            .Take(20)
            .ToListAsync(cancellationToken);
        if (stockErrors.Count > 0)
            problems.Add(new("stock-push", $"{stockErrors.Count} {(stockErrors.Count == 1 ? "publicación no aceptó" : "publicaciones no aceptaron")} el stock",
                $"«{Short(stockErrors[0].Title, 50)}»: {Short(stockErrors[0].LastStockPushError, 120)}", "/Publications?filter=mismatch"));
        else
            checks.Add(new("Stock en Mercado Libre", true, "Sin errores en 24 horas"));

        // 3) Facturas que no se subieron a la compra.
        var invoiceUploads = await _context.SalesInvoices.AsNoTracking()
            .CountAsync(i => i.OrganizationId == org && i.Status == "issued" && i.Kind == "individual" &&
                             i.UploadedToMercadoLibreAt == null && i.UploadError != null && i.IssuedAt >= now.AddDays(-7), cancellationToken);
        if (invoiceUploads > 0)
            problems.Add(new("invoice-upload", $"{invoiceUploads} {(invoiceUploads == 1 ? "factura no se subió" : "facturas no se subieron")} a Mercado Libre",
                "El comprador no la ve en su compra. Ábrela y pulsa «Subir a Mercado Libre» o mándala por mensaje.", "/Invoices"));

        // 3b) Mercado Pago sin permiso para leer los pagos.
        var mpDenied = await _context.SalesOrders.AsNoTracking()
            .CountAsync(o => o.OrganizationId == org && o.MpError != null && o.MpError.Contains("permiso") && o.MpCheckedAt >= now.AddDays(-2), cancellationToken);
        if (mpDenied > 0)
            problems.Add(new("mp-permission", "Mercado Pago no deja leer los pagos",
                "En el panel de desarrolladores de Mercado Libre dale a la app permiso de Mercado Pago y vuelve a conectar la cuenta.", "/Money"));

        // 4) Llaves que faltan para lo que sí se usa.
        var claude = _services.GetRequiredService<MarketplaceSync.Services.Ai.ClaudeClient>();
        if (!claude.IsConfigured)
            problems.Add(new("ai-key", "La IA está apagada", "Falta Anthropic__ApiKey en Render: no hay respuestas sugeridas, títulos, atributos ni asistente.", null));
        else
            checks.Add(new("IA", true, "Configurada"));

        var invoicing = await _context.InvoicingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == org, cancellationToken);
        var facturama = _services.GetRequiredService<MarketplaceSync.Services.Invoicing.FacturamaClient>();
        if (invoicing?.CsdUploadedAt is not null && !facturama.IsConfigured)
            problems.Add(new("facturama-key", "Facturama no está configurado", "Subiste tu CSD pero faltan Facturama__User y Facturama__Password en Render: no se puede timbrar.", "/Invoices/Settings"));
        else if (invoicing?.CsdUploadedAt is not null)
            checks.Add(new("Facturación", true, facturama.IsSandbox ? "Lista (modo de pruebas)" : "Lista"));

        // 5) Trabajos en segundo plano que fallaron en 24 horas (Hangfire). Solo nombres, sin datos.
        try
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var failed = monitor.FailedJobs(0, 100)
                .Where(j => j.Value?.FailedAt is { } at && at >= since)
                .Select(j => JobName(j.Value!.Job?.Method?.Name))
                .ToList();
            if (failed.Count > 0)
                problems.Add(new("jobs-failed", $"{failed.Count} {(failed.Count == 1 ? "tarea automática falló" : "tareas automáticas fallaron")} en 24 horas",
                    string.Join(", ", failed.GroupBy(n => n).OrderByDescending(g => g.Count()).Take(4).Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key)), null));
            else
                checks.Add(new("Tareas automáticas", true, "Sin fallas en 24 horas"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Sin almacenamiento de Hangfire (pruebas): no se revisa.
        }

        return (problems, checks);
    }

    /// <summary>Cómo se dice que ya se arregló (por el inicio de la clave).</summary>
    public static string ResolvedLabel(string key) => key.Split(':')[0] switch
    {
        "ml-token" => "La sesión de Mercado Libre volvió a funcionar",
        "ml-orders-error" or "ml-orders-stale" => "Los pedidos se vuelven a sincronizar",
        "ml-none" => "Ya hay una cuenta de Mercado Libre conectada",
        "stock-push" => "Mercado Libre ya acepta el stock",
        "invoice-upload" => "Las facturas ya se subieron a Mercado Libre",
        "ai-key" => "La IA ya está encendida",
        "facturama-key" => "Facturama ya está configurado",
        "jobs-failed" => "Las tareas automáticas ya no fallan",
        "mp-permission" => "Mercado Pago ya deja leer los pagos",
        _ => "Ya quedó"
    };

    private static string JobName(string? method) => method switch
    {
        "SyncMercadoLibreOrders" or "SyncOrdersForOrganization" => "pedidos",
        "SyncQuestions" or "SyncMercadoLibreQuestions" => "preguntas",
        "SyncPostSale" or "SyncPostSaleForOrganization" => "mensajes y reclamos",
        "SyncListingsForOrganization" or "SyncMercadoLibreListings" => "publicaciones",
        "SweepTelegramNotifications" or "PollTelegram" => "Telegram",
        "UpdateExchangeRate" => "tipo de cambio",
        "RunImportAgent" or "RunImportAgentRequest" => "agente de importación",
        "ApplyAsync" => "cambios por Excel",
        "RefreshAllAsync" => "revisión de promociones",
        null => "tarea",
        _ => method
    };

    private static string Hours(TimeSpan span) => span.TotalHours >= 48 ? $"{(int)span.TotalDays} días" : $"{(int)span.TotalHours} horas";
    private static string Minutes(TimeSpan span) => span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalHours} h";
    private static string Short(string? text, int max) => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..(max - 1)] + "…";
}
