namespace FabrCore.Services.Memory.Audit;

/// <summary>
/// One row in <c>mem.MemoryAuditLog</c> — a record of a memory-changing action.
/// </summary>
public sealed record MemoryAuditEntry
{
    /// <summary>Identity id (populated when read back from the table).</summary>
    public long AuditId { get; init; }

    /// <summary>When the action occurred (UTC). Defaults to now on insert.</summary>
    public DateTime OccurredAt { get; init; }

    /// <summary>
    /// The action kind: MemorySaved | MemoryMerged | MemoryUpdated | MemoryForgotten |
    /// MemoriesExtracted | ScopeConsolidated | ScopeCreated | ScopeDeleted |
    /// AdminCreated | AdminUpdated | AdminDeleted.
    /// </summary>
    public required string ActionType { get; init; }

    /// <summary>The memory scope the action applied to.</summary>
    public required string ScopeKey { get; init; }

    /// <summary>The affected memory id, when the action targets a single memory.</summary>
    public Guid? MemoryId { get; init; }

    /// <summary>Agent handle or admin user id that performed the action.</summary>
    public string? ActorId { get; init; }

    /// <summary>Display name of the actor, when known.</summary>
    public string? ActorName { get; init; }

    /// <summary>Short human-readable summary (e.g. the memory title).</summary>
    public string? Summary { get; init; }

    /// <summary>Optional JSON detail payload.</summary>
    public string? Payload { get; init; }

    /// <summary>How long the action took, when measured.</summary>
    public long? DurationMs { get; init; }
}
