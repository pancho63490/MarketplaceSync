using MarketplaceSync.Web.Models.Marketplace;

namespace MarketplaceSync.Web.Models.Questions;

/// <summary>Pregunta de un comprador en una publicación de Mercado Libre.</summary>
public sealed class BuyerQuestion
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public int MarketplaceAccountId { get; set; }
    public MarketplaceAccount MarketplaceAccount { get; set; } = null!;

    public string ExternalQuestionId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>UNANSWERED, ANSWERED, CLOSED_UNANSWERED, UNDER_REVIEW, BANNED, DELETED.</summary>
    public string Status { get; set; } = BuyerQuestionStatus.Unanswered;
    public DateTime DateCreated { get; set; }

    /// <summary>Respuesta propuesta por la IA (se edita antes de enviar).</summary>
    public string? SuggestedAnswer { get; set; }
    public DateTime? SuggestedAt { get; set; }

    public string? AnswerText { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public string? LastError { get; set; }

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}

public static class BuyerQuestionStatus
{
    public const string Unanswered = "UNANSWERED";
    public const string Answered = "ANSWERED";
}
