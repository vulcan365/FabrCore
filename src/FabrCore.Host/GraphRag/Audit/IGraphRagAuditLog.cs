namespace FabrCore.Services.GraphRag.Audit;

/// <summary>
/// Writes audit rows to <c>grag.ActionAudit</c>. All methods are best-effort —
/// implementations are expected to swallow database errors and only log
/// internally so audit failures never fail the underlying user action.
///
/// <para>
/// Service-specific overloads (<see cref="RecordSearchAsync"/>, etc.) are
/// thin wrappers around the low-level <see cref="RecordAsync"/> that fill in
/// the right <c>ActionType</c>, <c>SubjectKind</c>, and JSON payload shape.
/// Add new convenience methods here as new auditable actions are introduced;
/// keep the JSON payload shapes documented in their summaries.
/// </para>
/// </summary>
public interface IGraphRagAuditLog
{
    /// <summary>Low-level write. Returns once the row is inserted (or the failure is logged).</summary>
    Task RecordAsync(GraphRagAuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Records a search. <c>ActionType = "SearchExecuted"</c>. Payload JSON
    /// includes <c>{ query, scopes, limit, resultCount, embeddingModel? }</c>.
    /// </summary>
    Task RecordSearchAsync(
        string query,
        IReadOnlyList<string> scopes,
        int limit,
        int resultCount,
        long durationMs,
        string searchKind, // 'Entities' | 'Chunks' | 'Relationships' | 'Hybrid'
        string? actorId = null,
        string? actorName = null,
        Guid? correlationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records the creation of a knowledge scope.
    /// <c>ActionType = "ScopeCreated"</c>.
    /// </summary>
    Task RecordScopeCreatedAsync(
        string scopeKey,
        string? description,
        double defaultPriority,
        string? actorId = null,
        string? actorName = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records a successful document ingest (or re-ingest).
    /// <c>ActionType = "DocumentIngested"</c>.
    /// </summary>
    Task RecordDocumentIngestedAsync(
        Guid documentId,
        string fileName,
        string scopeKey,
        int versionNumber,
        int chunkCount,
        int extractedEntityCount,
        int extractedRelationshipCount,
        long durationMs,
        string? actorId = null,
        string? actorName = null,
        Guid? correlationId = null,
        CancellationToken ct = default,
        IngestionAuditMetrics? performance = null);

    /// <summary>
    /// Records a document deletion.
    /// <c>ActionType = "DocumentDeleted"</c>.
    /// </summary>
    Task RecordDocumentDeletedAsync(
        Guid documentId,
        string fileName,
        string scopeKey,
        int contributionsProcessed,
        string? actorId = null,
        string? actorName = null,
        CancellationToken ct = default);
}

public sealed record IngestionAuditMetrics(
    string? ResolvedModelName,
    int ChatCallCount,
    int EmbeddingBatchCount,
    int SqlCommandBatchCount,
    long ChunkEmbeddingMs,
    long DocumentEmbeddingMs,
    long LlmExtractionMs,
    long EntityEmbeddingMs,
    long SqlWriteMs,
    long ChatInputTokens = 0,
    long ChatOutputTokens = 0,
    long ChatTotalMs = 0,
    int ExtractionBatchCount = 0,
    int ExtractionRetryCount = 0,
    int ExtractionTruncationCount = 0,
    string? ResolvedProviderName = null,
    string? ResolvedDeploymentModelName = null,
    IReadOnlyList<string>? FinishReasons = null);
