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

    // ── Fotos ──

    /// <summary>El piloto automático pone fondo blanco a la portada antes de publicar.</summary>
    public bool FixPhotosOnPublish { get; set; } = true;

    // ── Mensaje de gracias ──

    public bool ThankYouEnabled { get; set; }

    [MaxLength(350)]
    public string ThankYouMessage { get; set; } = AutomationDefaults.ThankYou;

    /// <summary>Desde cuándo está prendido: solo se manda a ventas posteriores.</summary>
    public DateTime? ThankYouSince { get; set; }

    // ── Precios ──

    /// <summary>Qué hacer cuando una publicación deja de ganar lo mínimo: off, alert (avisar con botones) o auto.</summary>
    [MaxLength(10)]
    public string RepricingMode { get; set; } = RepricingModes.Alert;

    /// <summary>En automático, cuánto puede subir un precio sin preguntar (en %); arriba de eso solo avisa.</summary>
    public decimal RepricingMaxIncreasePercent { get; set; } = 25m;

    // ── Etiquetas ──

    /// <summary>Mandar cada mañana a Telegram las etiquetas y la lista de qué empacar.</summary>
    public bool LabelsDaily { get; set; } = true;

    /// <summary>Hora de Juárez para mandar las etiquetas.</summary>
    public int LabelsHour { get; set; } = 9;

    public DateOnly? LabelsSentOn { get; set; }

    /// <summary>Etiquetas en formato para impresora térmica (ZPL/10x15) en lugar de PDF carta.</summary>
    public bool LabelsThermal { get; set; }

    // ── Compra semanal ──

    /// <summary>Mandar cada semana la lista de qué recomprar en EE.UU. para aprobarla en Telegram.</summary>
    public bool WeeklyPurchaseEnabled { get; set; } = true;

    /// <summary>Día de la semana (0 = domingo … 6 = sábado) a las 8:00 de Juárez.</summary>
    public int WeeklyPurchaseDay { get; set; } = 1;

    /// <summary>Incluir también los productos nuevos que recomienda "Qué comprar" (máximo 3).</summary>
    public bool WeeklyPurchaseIncludeNew { get; set; } = true;

    public DateOnly? WeeklyPurchaseSentOn { get; set; }

    // ── Promociones ──

    /// <summary>Avisar por Telegram cuando Mercado Libre invita a una campaña, con botón para entrar con las que siguen ganando.</summary>
    public bool PromotionAlerts { get; set; } = true;

    /// <summary>Última vez que se revisaron campañas nuevas (una vez al día).</summary>
    public DateOnly? PromotionsCheckedOn { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class RepricingModes
{
    public const string Off = "off";
    public const string Alert = "alert";
    public const string Auto = "auto";
}

public static class AutomationDefaults
{
    public const string ThankYou =
        "¡Hola! Gracias por tu compra. Ya estamos preparando tu pedido y te avisamos por aquí cuando salga. Si tienes cualquier duda, escríbenos en este chat. ¡Saludos!";
}
