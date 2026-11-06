using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Automation;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Automation;

public sealed record AnswerTopic(string Id, string Label, string Example, bool CanAuto, string? WhyNot = null);

/// <summary>Ajustes de lo que la app hace sola.</summary>
public sealed class AutomationService
{
    private readonly AppDbContext _context;

    public AutomationService(AppDbContext context) => _context = context;

    /// <summary>Temas de preguntas. Compatibilidad, precio y lo demás nunca se responden solos.</summary>
    public static readonly AnswerTopic[] Topics =
    {
        new("disponibilidad", "Disponibilidad y piezas", "¿Tienes 2? ¿Hay en existencia?", true),
        new("envio", "Envío", "¿Hacen envíos? ¿Cuánto tarda?", true),
        new("condicion", "Nuevo y original", "¿Es nuevo? ¿Es original?", true),
        new("garantia", "Garantía", "¿Tiene garantía?", true),
        new("recoger", "Entrega en persona", "¿Puedo pasar a recogerlo?", true),
        new("factura", "Factura", "¿Dan factura?", true),
        new("compatibilidad", "Compatibilidad", "¿Le queda a mi Silverado 2016?", false, "Si la IA se equivoca, el comprador recibe una pieza que no le queda: siempre pasa por ti."),
        new("precio", "Precio y descuentos", "¿Me lo dejas en menos?", false, "Negociar es decisión tuya."),
        new("otro", "Otras", "Todo lo demás", false, "Pasan por ti.")
    };

    public static string TopicLabel(string? id) => Topics.FirstOrDefault(t => t.Id == id)?.Label ?? "Sin tema";

    public async Task<AutomationSettings> GetAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var settings = await _context.AutomationSettings.FirstOrDefaultAsync(s => s.OrganizationId == org, cancellationToken);
        if (settings is not null)
            return settings;
        settings = new AutomationSettings { OrganizationId = org };
        _context.AutomationSettings.Add(settings);
        await _context.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public static HashSet<string> EnabledTopics(AutomationSettings settings) =>
        settings.AutoAnswerTopics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => Topics.Any(x => x.Id == t && x.CanAuto))
            .ToHashSet();
}
