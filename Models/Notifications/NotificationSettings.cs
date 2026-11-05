using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Notifications;

/// <summary>
/// Avisos por Telegram de una organización: el bot, el chat vinculado y qué avisar.
/// El token del bot nunca se muestra en pantalla; si existe la variable Telegram__BotToken, esa tiene prioridad.
/// </summary>
public sealed class NotificationSettings
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    [MaxLength(200)]
    public string? TelegramBotToken { get; set; }

    [MaxLength(100)]
    public string? TelegramBotUsername { get; set; }

    public long? TelegramChatId { get; set; }

    [MaxLength(200)]
    public string? TelegramChatName { get; set; }

    /// <summary>Código de un solo uso para vincular el chat con /start CODIGO.</summary>
    [MaxLength(40)]
    public string? LinkCode { get; set; }

    public DateTime? LinkedAt { get; set; }

    /// <summary>Último update_id procesado de getUpdates.</summary>
    public long UpdateOffset { get; set; }

    /// <summary>Dirección pública de la app, para los enlaces de los mensajes.</summary>
    [MaxLength(300)]
    public string? PublicBaseUrl { get; set; }

    public bool NotifyQuestions { get; set; } = true;
    public bool NotifyOrders { get; set; } = true;
    public bool NotifyStock { get; set; } = true;
    public bool DailySummary { get; set; } = true;

    /// <summary>Hora de Ciudad Juárez para el resumen diario (0-23).</summary>
    public int DailySummaryHour { get; set; } = 8;
    public DateOnly? LastDailySummaryOn { get; set; }

    [MaxLength(300)]
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Registro de avisos enviados, para no repetirlos y para saber a qué mensaje responde el vendedor.</summary>
public sealed class NotificationLog
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Clave única del aviso: "question:123", "order:456", "out:78:2026-11-04", "summary:2026-11-04".</summary>
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(30)]
    public string Kind { get; set; } = string.Empty;

    [MaxLength(60)]
    public string? EntityId { get; set; }

    public long? TelegramChatId { get; set; }
    public long? TelegramMessageId { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

public static class NotificationKind
{
    public const string Question = "question";
    public const string Order = "order";
    public const string OutOfStock = "out";
    public const string Summary = "summary";
    public const string Test = "test";
}
