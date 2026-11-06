using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Automation;

/// <summary>Qué hace la app sola, por organización (pantalla Configuración → Automático).</summary>
public sealed class AutomationSettings
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    // ── Preguntas ──

    /// <summary>Responder sola las preguntas fáciles (las de los temas elegidos y que la IA marca como seguras).</summary>
    public bool AutoAnswerEnabled { get; set; }

    /// <summary>Temas que se responden solos, separados por coma (ver AutomationService.Topics).</summary>
    [MaxLength(200)]
    public string AutoAnswerTopics { get; set; } = "disponibilidad,envio,condicion,garantia,recoger";

    /// <summary>Desde cuándo está prendido: las preguntas anteriores no se responden solas.</summary>
    public DateTime? AutoAnswerSince { get; set; }

    /// <summary>¿Das factura? null = no se ha dicho (las preguntas de factura no se responden solas).</summary>
    public bool? OffersInvoice { get; set; }

    // ── Mensaje de gracias ──

    public bool ThankYouEnabled { get; set; }

    [MaxLength(350)]
    public string ThankYouMessage { get; set; } = AutomationDefaults.ThankYou;

    /// <summary>Desde cuándo está prendido: solo se manda a ventas posteriores.</summary>
    public DateTime? ThankYouSince { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class AutomationDefaults
{
    public const string ThankYou =
        "¡Hola! Gracias por tu compra. Ya estamos preparando tu pedido y te avisamos por aquí cuando salga. Si tienes cualquier duda, escríbenos en este chat. ¡Saludos!";
}
