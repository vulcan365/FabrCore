namespace FabrCore.Services.GraphRag.Audit;

/// <summary>
/// Severity level for an audit row. Currently only the value is stored; future
/// admin UIs can drive coloring or filtering off this.
/// </summary>
public enum AuditSeverity : byte
{
    Info = 0,
    Warn = 1,
    Error = 2
}

/// <summary>
/// One row destined for <c>grag.ActionAudit</c>. All fields are optional except
/// <see cref="ActionType"/>, which is required and SHOULD be a stable
/// machine-readable token (e.g. <c>"SearchExecuted"</c>, <c>"DocumentDeleted"</c>).
///
/// <para>
/// <see cref="Payload"/> is free-form JSON and SHOULD only carry serialized
/// structures — it is written to the database as a string. Hot/queryable
/// fields (token counts, duration) are first-class columns elsewhere on this
/// record so they don't have to be parsed out of JSON.
/// </para>
/// </summary>
public sealed record GraphRagAuditEntry
{
    /// <summary>Stable machine-readable action token. Required.</summary>
    public required string ActionType { get; init; }

    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;

    public string? ActorKind { get; init; }
    public string? ActorId { get; init; }
    public string? ActorName { get; init; }

    public string? SubjectKind { get; init; }
    public string? SubjectId { get; init; }

    public string? ScopeKey { get; init; }
    public Guid? CorrelationId { get; init; }

    public long? DurationMs { get; init; }
    public string? Summary { get; init; }

    /// <summary>
    /// Free-form JSON payload. The audit service serializes this directly into
    /// the <c>Payload NVARCHAR(MAX)</c> column without re-validation, so the
    /// caller is responsible for producing valid JSON.
    /// </summary>
    public string? Payload { get; init; }
}
