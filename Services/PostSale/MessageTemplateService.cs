using System.Text;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.PostSale;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.PostSale;

/// <summary>
/// Mensajes rápidos de postventa: los escribe el vendedor (con {comprador} y {producto}), se usan en la conversación
/// del pedido y en Telegram, y la IA elige cuál contesta mejor el mensaje del comprador.
/// </summary>
public sealed class MessageTemplateService
{
    public const int MaxTemplates = 20;

    private static readonly (string Title, string Text)[] Defaults =
    {
        ("Gracias por tu compra", "¡Gracias por tu compra, {comprador}! Ya estamos preparando tu pedido y te aviso en cuanto salga."),
        ("Ya va en camino", "Tu pedido ya va en camino. Puedes seguirlo desde tu compra en Mercado Libre. ¡Saludos!"),
        ("Confirmar vehículo", "Para asegurar que te quede, ¿me confirmas el año, modelo y motor de tu vehículo?"),
        ("¿Ya te llegó?", "¿Ya te llegó tu {producto}? Cualquier duda con la instalación aquí estoy para ayudarte."),
        ("Factura", "Claro, con gusto te facturo. Agrega tus datos fiscales en la compra (Mercado Libre te pide RFC y régimen) y te la subo ahí mismo."),
        ("Lamento el problema", "Lamento el inconveniente. Cuéntame qué pasó y con gusto lo resolvemos.")
    };

    private readonly AppDbContext _context;
    private readonly MarketplaceSync.Services.Ai.ClaudeClient _claude;
    private readonly ILogger<MessageTemplateService> _logger;
    private readonly PostSaleService _postSale;

    public MessageTemplateService(AppDbContext context, MarketplaceSync.Services.Ai.ClaudeClient claude, PostSaleService postSale, ILogger<MessageTemplateService> logger)
    {
        _postSale = postSale;
        _context = context;
        _claude = claude;
        _logger = logger;
    }

    /// <summary>Los mensajes rápidos de la cuenta; la primera vez se crean los de ejemplo.</summary>
    public async Task<List<MessageTemplate>> ListAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var list = await _context.MessageTemplates.Where(t => t.OrganizationId == org)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(cancellationToken);
        if (list.Count > 0) return list;
        // Sin ninguno (primera vez, o si borró todos) se crean los de ejemplo.
        var order = 0;
        foreach (var (title, text) in Defaults)
            _context.MessageTemplates.Add(new MessageTemplate { OrganizationId = org, Title = title, Text = text, SortOrder = order++ });
        await _context.SaveChangesAsync(cancellationToken);
        return await _context.MessageTemplates.Where(t => t.OrganizationId == org)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(cancellationToken);
    }

    public async Task<(bool Ok, string Message)> SaveAsync(Guid org, int? id, string? title, string? text, CancellationToken cancellationToken = default)
    {
        title = title?.Trim();
        text = text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return (false, "Ponle un nombre corto, por ejemplo «Ya va en camino».");
        if (string.IsNullOrWhiteSpace(text)) return (false, "Escribe el mensaje.");
        if (title.Length > 60) title = title[..60];
        if (text.Length > PostSaleService.MaxMessageLength) return (false, $"El mensaje es muy largo: máximo {PostSaleService.MaxMessageLength} letras.");
        if (_postSale.HasContactData(text)) return (false, "Quita teléfonos, correos o links: Mercado Libre bloquea esos mensajes.");

        MessageTemplate? template;
        if (id is { } existing)
        {
            template = await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == existing && t.OrganizationId == org, cancellationToken);
            if (template is null) return (false, "No encontré ese mensaje.");
        }
        else
        {
            if (await _context.MessageTemplates.CountAsync(t => t.OrganizationId == org, cancellationToken) >= MaxTemplates)
                return (false, $"Puedes tener hasta {MaxTemplates} mensajes rápidos.");
            var next = await _context.MessageTemplates.Where(t => t.OrganizationId == org).MaxAsync(t => (int?)t.SortOrder, cancellationToken) ?? -1;
            template = new MessageTemplate { OrganizationId = org, SortOrder = next + 1 };
            _context.MessageTemplates.Add(template);
        }
        template.Title = title;
        template.Text = text;
        template.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return (true, id is null ? $"Mensaje «{title}» agregado." : $"Mensaje «{title}» guardado.");
    }

    public async Task<bool> DeleteAsync(Guid org, int id, CancellationToken cancellationToken = default) =>
        await _context.MessageTemplates.Where(t => t.Id == id && t.OrganizationId == org).ExecuteDeleteAsync(cancellationToken) > 0;

    /// <summary>Cambia {comprador} y {producto} por los datos del pedido.</summary>
    public static string Render(string text, SalesOrder? order)
    {
        var buyer = order?.BuyerNickname is { Length: > 0 } nick ? nick : "";
        var product = order?.Items.FirstOrDefault()?.Title is { Length: > 0 } title ? Shorten(title, 60) : "pedido";
        var rendered = text.Replace("{comprador}", buyer, StringComparison.OrdinalIgnoreCase)
            .Replace("{producto}", product, StringComparison.OrdinalIgnoreCase);
        // "¡Gracias por tu compra, !" si no hay nombre.
        rendered = rendered.Replace(" , ", " ").Replace(", !", "!").Replace(", .", ".").Replace("  ", " ").Trim();
        return rendered.Length > PostSaleService.MaxMessageLength ? rendered[..PostSaleService.MaxMessageLength] : rendered;
    }

    public async Task MarkUsedAsync(Guid org, int id, CancellationToken cancellationToken = default) =>
        await _context.MessageTemplates.Where(t => t.Id == id && t.OrganizationId == org)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.UseCount, t => t.UseCount + 1), cancellationToken);

    /// <summary>
    /// Elige el mensaje rápido que mejor contesta lo que escribió el comprador. Con IA pregunta cuál (o ninguno);
    /// sin IA regresa null y se muestran los más usados.
    /// </summary>
    public async Task<MessageTemplate?> PickAsync(IReadOnlyList<MessageTemplate> templates, string buyerText, CancellationToken cancellationToken = default)
    {
        if (!_claude.IsConfigured || templates.Count == 0 || string.IsNullOrWhiteSpace(buyerText)) return null;
        var prompt = new StringBuilder("Mensaje del comprador:\n").AppendLine(buyerText.Length > 600 ? buyerText[..600] : buyerText)
            .AppendLine().AppendLine("Respuestas guardadas:");
        for (var i = 0; i < templates.Count; i++)
            prompt.Append(i + 1).Append(". ").Append(templates[i].Title).Append(": ").AppendLine(templates[i].Text);
        try
        {
            var answer = await _claude.CompleteAsync(
                "Eres el vendedor de una tienda de refacciones en Mercado Libre México. Elige la respuesta guardada que contesta " +
                "directamente el mensaje del comprador. Responde solo con el número; responde 0 si ninguna lo contesta bien.",
                prompt.ToString(), 5, 0, cancellationToken);
            var digits = new string((answer ?? string.Empty).Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) && n >= 1 && n <= templates.Count ? templates[n - 1] : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Template pick failed.");
            return null;
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
