using System.ComponentModel.DataAnnotations;

namespace MarketplaceSync.Web.Models.Agents;

/// <summary>Una corrida del agente de importación: qué revisó, qué importó y qué descartó.</summary>
public sealed class AgentRun
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>schedule (diario) o manual (botón "Ejecutar ahora").</summary>
    [MaxLength(20)]
    public string Trigger { get; set; } = "schedule";

    /// <summary>running, done, skipped, failed.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = AgentRunStatus.Running;

    /// <summary>ai (agente con Claude) o rules (sin IA configurada: los de más demanda y ganancia).</summary>
    [MaxLength(10)]
    public string Engine { get; set; } = "ai";

    public int Reviewed { get; set; }
    public int Imported { get; set; }
    public int Discarded { get; set; }

    [MaxLength(2000)]
    public string? Summary { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    public ICollection<AgentStep> Steps { get; set; } = new List<AgentStep>();
}

/// <summary>Una decisión del agente con su razón.</summary>
public sealed class AgentStep
{
    public long Id { get; set; }
    public long AgentRunId { get; set; }
    public AgentRun AgentRun { get; set; } = null!;

    /// <summary>imported, discarded, skipped, note.</summary>
    [MaxLength(20)]
    public string Action { get; set; } = string.Empty;

    public long? CandidateId { get; set; }
    public int? ProductId { get; set; }

    [MaxLength(300)]
    public string? Title { get; set; }

    [MaxLength(1000)]
    public string? Reason { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

public static class AgentRunStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}
