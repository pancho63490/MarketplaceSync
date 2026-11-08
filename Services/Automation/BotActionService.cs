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

    public BotActionService(AppDbContext context, MercadoLibreItemEditorService editor, ILogger<BotActionService> logger)
    {
        _context = context;
        _editor = editor;
        _logger = logger;
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

    private async Task<string?> PriceAsync(Guid org, BotAction action, CancellationToken cancellationToken)
    {
        var result = await _editor.UpdateAsync(org, action.PublicationId!.Value,
            new ItemEdit { Price = action.Value!.Value.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
        return result.Ok ? null : string.Join(" ", result.Errors);
    }

    public static string Money(decimal? value) => value.HasValue ? value.Value.ToString("C0", Mx) : "—";
}
