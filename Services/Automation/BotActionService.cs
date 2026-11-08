using System.Globalization;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Automation;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Automation;

/// <summary>
/// Acciones que esperan confirmación en Telegram (precio, pausar, activar). Ejecuta solo las pendientes,
/// de la misma organización y de menos de 48 horas.
/// </summary>
public sealed class BotActionService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly MercadoLibreItemEditorService _editor;
    private readonly ILogger<BotActionService> _logger;

    private readonly IServiceProvider _services;

    public BotActionService(AppDbContext context, MercadoLibreItemEditorService editor, ILogger<BotActionService> logger, IServiceProvider services)
    {
        _context = context;
        _editor = editor;
        _logger = logger;
        _services = services;
    }

    public async Task<BotAction> ProposeAsync(Guid org, string kind, int publicationId, decimal? value, string description, string source, CancellationToken cancellationToken = default)
    {
        // Una sola propuesta pendiente por publicación: la nueva reemplaza a la anterior.
        var previous = await _context.BotActions
            .Where(a => a.OrganizationId == org && a.PublicationId == publicationId && a.Status == BotActionStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var old in previous)
        {
            old.Status = BotActionStatus.Expired;
            old.DecidedAt = DateTime.UtcNow;
        }

        var action = new BotAction
        {
            OrganizationId = org,
            Kind = kind,
            PublicationId = publicationId,
            Value = value,
            Description = description.Length > 400 ? description[..400] : description,
            Source = source
        };
        _context.BotActions.Add(action);
        await _context.SaveChangesAsync(cancellationToken);
        return action;
    }

    /// <summary>Ejecuta la acción y regresa el texto para el vendedor.</summary>
    public async Task<(bool Ok, string Message)> ExecuteAsync(Guid org, long actionId, CancellationToken cancellationToken = default)
    {
        var action = await _context.BotActions.FirstOrDefaultAsync(a => a.Id == actionId && a.OrganizationId == org, cancellationToken);
        if (action is null)
            return (false, "Ya no encontré esa acción.");
        if (action.Status != BotActionStatus.Pending)
            return (false, action.Status == BotActionStatus.Done ? "Eso ya estaba hecho." : "Esa propuesta ya no está vigente.");
        if (DateTime.UtcNow - action.CreatedAt > Lifetime)
        {
            action.Status = BotActionStatus.Expired;
            action.DecidedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return (false, "Esa propuesta ya venció (más de 48 horas). Pídemela de nuevo.");
        }

        string? error;
        try
        {
            error = action.Kind switch
            {
                BotActionKinds.Price when action.Value is > 0 => await PriceAsync(org, action, cancellationToken),
                BotActionKinds.Pause => await _editor.SetStatusAsync(org, action.PublicationId!.Value, "paused", cancellationToken),
                BotActionKinds.Activate => await _editor.SetStatusAsync(org, action.PublicationId!.Value, "active", cancellationToken),
                BotActionKinds.Compatibilities => await CompatibilitiesAsync(org, action, cancellationToken),
                BotActionKinds.Discount when action.Value is > 0 => await DiscountAsync(org, action, cancellationToken),
                BotActionKinds.DiscountRemove => await LeavePromotionAsync(org, action, cancellationToken),
                BotActionKinds.PromotionJoin => await JoinPromotionAsync(org, action, cancellationToken),
                BotActionKinds.Invoice => await InvoiceAsync(org, action, cancellationToken),
                BotActionKinds.GlobalInvoice => await GlobalInvoiceAsync(org, action, cancellationToken),
                _ => "No sé hacer esa acción."
            };
        }
        catch (MercadoLibreEditException exception)
        {
            error = exception.Message;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Bot action {ActionId} failed.", actionId);
            error = "Mercado Libre no respondió. Intenta de nuevo en un momento.";
        }

        action.Status = error is null ? BotActionStatus.Done : BotActionStatus.Failed;
        action.Result = error;
        action.DecidedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return error is null ? (true, "Hecho: " + action.Description) : (false, error);
    }

    public async Task<string> CancelAsync(Guid org, long actionId, CancellationToken cancellationToken = default)
    {
        var action = await _context.BotActions.FirstOrDefaultAsync(a => a.Id == actionId && a.OrganizationId == org, cancellationToken);
        if (action is null || action.Status != BotActionStatus.Pending)
            return "Esa propuesta ya no está vigente.";
        action.Status = BotActionStatus.Cancelled;
        action.DecidedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return "Cancelado: no cambié nada.";
    }

    private async Task<string?> CompatibilitiesAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        var compat = _services.GetRequiredService<CompatibilityService>();
        var (ok, _, message) = await compat.SendToItemAsync(org, action.PublicationId!.Value, replace: false, cancellationToken);
        if (ok)
            action.Description = message;
        return ok ? null : message;
    }

    private async Task<string?> DiscountAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        var days = 7;
        if (!string.IsNullOrWhiteSpace(action.Payload))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(action.Payload);
                if (document.RootElement.TryGetProperty("days", out var d) && d.TryGetInt32(out var n)) days = n;
            }
            catch (System.Text.Json.JsonException) { }
        }
        var (ok, message) = await _services.GetRequiredService<PromotionService>()
            .CreateDiscountAsync(org, action.PublicationId!.Value, (int)action.Value!.Value, days, cancellationToken);
        if (ok) action.Description = message;
        return ok ? null : message;
    }

    private async Task<string?> LeavePromotionAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        var (ok, message) = await _services.GetRequiredService<PromotionService>().LeaveAsync(org, action.PublicationId!.Value, cancellationToken);
        if (ok) action.Description = message;
        return ok ? null : message;
    }

    private async Task<string?> JoinPromotionAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        PromotionJoinPayload? payload = null;
        try { payload = string.IsNullOrWhiteSpace(action.Payload) ? null : System.Text.Json.JsonSerializer.Deserialize<PromotionJoinPayload>(action.Payload); }
        catch (System.Text.Json.JsonException) { }
        if (payload is null || payload.Items.Count == 0)
            return "No encontré qué publicaciones meter a la campaña.";

        var (joined, errors) = await _services.GetRequiredService<PromotionService>().JoinManyAsync(org, payload, cancellationToken);
        if (joined == 0)
            return "Mercado Libre no aceptó ninguna. " + string.Join(" · ", errors.Take(3));
        action.Description = $"Entraron {joined} de {payload.Items.Count} a «{payload.Name}»." +
                             (errors.Count > 0 ? " No entraron: " + string.Join(" · ", errors.Take(3)) : string.Empty);
        if (action.Description.Length > 400) action.Description = action.Description[..400];
        return null;
    }

    private static int PayloadInt(BotAction action, string name)
    {
        if (string.IsNullOrWhiteSpace(action.Payload)) return 0;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(action.Payload);
            return document.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
        }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    private async Task<string?> InvoiceAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        var (ok, message) = await _services.GetRequiredService<MarketplaceSync.Services.Invoicing.InvoicingService>()
            .IssueFromBuyerDataAsync(org, PayloadInt(action, "orderId"), cancellationToken);
        if (ok) action.Description = message.Length > 400 ? message[..400] : message;
        return ok ? null : message;
    }

    private async Task<string?> GlobalInvoiceAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        var (ok, message, _) = await _services.GetRequiredService<MarketplaceSync.Services.Invoicing.InvoicingService>()
            .IssueGlobalAsync(org, PayloadInt(action, "year"), PayloadInt(action, "month"), includeBuyersWithRfc: false, cancellationToken);
        if (ok) action.Description = message.Length > 400 ? message[..400] : message;
        return ok ? null : message;
    }

    private async Task<string?> PriceAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        var result = await _editor.UpdateAsync(org, action.PublicationId!.Value,
            new ItemEdit { Price = action.Value!.Value.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
        return result.Ok ? null : string.Join(" ", result.Errors);
    }

    public static string Money(decimal? value) => value.HasValue ? value.Value.ToString("C0", Mx) : "—";
}
