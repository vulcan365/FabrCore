using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Audit;

/// <summary>
/// SQL-backed implementation of <see cref="IGraphRagAuditLog"/>. Inserts rows
/// into <c>grag.ActionAudit</c> using a fresh <see cref="SqlConnection"/> per
/// call. Audit writes never throw — every database error is logged and
/// swallowed so the calling user action is never disrupted by an audit
/// failure.
/// </summary>
public sealed class GraphRagAuditLog : IGraphRagAuditLog
{
    private readonly string _connectionString;
    private readonly ILogger<GraphRagAuditLog> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public GraphRagAuditLog(
        IConfiguration configuration,
        ILogger<GraphRagAuditLog> logger,
        string connectionStringName)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrWhiteSpace(connectionStringName))
            throw new ArgumentException("Connection string name is required", nameof(connectionStringName));

        _connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' not found in configuration");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordAsync(GraphRagAuditEntry entry, CancellationToken ct = default)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.ActionType))
            throw new ArgumentException("ActionType is required", nameof(entry));

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var sql = $"""
                INSERT INTO {GraphRagSchemaInitializer.SchemaName}.ActionAudit
                    (ActionType, Severity,
                     ActorKind, ActorId, ActorName,
                     SubjectKind, SubjectId,
                     ScopeKey, CorrelationId,
                     DurationMs, Summary, Payload)
                VALUES (@actionType, @severity,
                        @actorKind, @actorId, @actorName,
                        @subjectKind, @subjectId,
                        @scopeKey, @correlationId,
                        @durationMs, @summary, @payload);
                """;

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@actionType", entry.ActionType);
            cmd.Parameters.AddWithValue("@severity", (byte)entry.Severity);
            cmd.Parameters.AddWithValue("@actorKind", (object?)entry.ActorKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@actorId", (object?)entry.ActorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@actorName", (object?)entry.ActorName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@subjectKind", (object?)entry.SubjectKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@subjectId", (object?)entry.SubjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@scopeKey", (object?)entry.ScopeKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@correlationId", (object?)entry.CorrelationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@durationMs", (object?)entry.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@summary",
                entry.Summary is null ? DBNull.Value : Truncate(entry.Summary, 500));
            cmd.Parameters.AddWithValue("@payload", (object?)entry.Payload ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // Audit MUST NOT break the calling user action. Log and swallow.
            _logger.LogError(ex,
                "Failed to write GraphRAG audit row (ActionType={ActionType}, ActorId={ActorId})",
                entry.ActionType, entry.ActorId);
        }
    }

    public Task RecordSearchAsync(
        string query,
        IReadOnlyList<string> scopes,
        int limit,
        int resultCount,
        long durationMs,
        string searchKind,
        string? actorId = null,
        string? actorName = null,
        Guid? correlationId = null,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            searchKind,
            query,
            scopes,
            limit,
            resultCount
        }, JsonOptions);

        var summary = $"{searchKind} search ({resultCount} results): \"{Truncate(query, 200)}\"";

        return RecordAsync(new GraphRagAuditEntry
        {
            ActionType = "SearchExecuted",
            ActorKind = actorId is null ? "Service" : "User",
            ActorId = actorId,
            ActorName = actorName,
            SubjectKind = "SearchQuery",
            SubjectId = null,
            ScopeKey = scopes.Count == 1 ? scopes[0] : null,
            CorrelationId = correlationId,
            DurationMs = durationMs,
            Summary = summary,
            Payload = payload
        }, ct);
    }

    public Task RecordScopeCreatedAsync(
        string scopeKey,
        string? description,
        double defaultPriority,
        string? actorId = null,
        string? actorName = null,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            description,
            defaultPriority
        }, JsonOptions);

        return RecordAsync(new GraphRagAuditEntry
        {
            ActionType = "ScopeCreated",
            ActorKind = actorId is null ? "Service" : "User",
            ActorId = actorId,
            ActorName = actorName,
            SubjectKind = "Scope",
            SubjectId = scopeKey,
            ScopeKey = scopeKey,
            Summary = $"Created scope '{scopeKey}'",
            Payload = payload
        }, ct);
    }

    public Task RecordDocumentIngestedAsync(
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
        IngestionAuditMetrics? performance = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            fileName,
            versionNumber,
            chunkCount,
            extractedEntityCount,
            extractedRelationshipCount,
            resolvedModelName = performance?.ResolvedModelName,
            resolvedProviderName = performance?.ResolvedProviderName,
            resolvedDeploymentModelName = performance?.ResolvedDeploymentModelName,
            chatCallCount = performance?.ChatCallCount ?? 0,
            chatInputTokens = performance?.ChatInputTokens ?? 0,
            chatOutputTokens = performance?.ChatOutputTokens ?? 0,
            chatTotalMs = performance?.ChatTotalMs ?? 0,
            extractionBatchCount = performance?.ExtractionBatchCount ?? 0,
            extractionRetryCount = performance?.ExtractionRetryCount ?? 0,
            extractionTruncationCount = performance?.ExtractionTruncationCount ?? 0,
            finishReasons = performance?.FinishReasons ?? [],
            embeddingBatchCount = performance?.EmbeddingBatchCount ?? 0,
            sqlCommandBatchCount = performance?.SqlCommandBatchCount ?? 0,
            chunkEmbeddingMs = performance?.ChunkEmbeddingMs ?? 0,
            documentEmbeddingMs = performance?.DocumentEmbeddingMs ?? 0,
            llmExtractionMs = performance?.LlmExtractionMs ?? 0,
            entityEmbeddingMs = performance?.EntityEmbeddingMs ?? 0,
            sqlWriteMs = performance?.SqlWriteMs ?? 0
        }, JsonOptions);

        return RecordAsync(new GraphRagAuditEntry
        {
            ActionType = "DocumentIngested",
            ActorKind = actorId is null ? "Service" : "User",
            ActorId = actorId,
            ActorName = actorName,
            SubjectKind = "Document",
            SubjectId = documentId.ToString(),
            ScopeKey = scopeKey,
            CorrelationId = correlationId,
            DurationMs = durationMs,
            Summary = $"Ingested '{fileName}' v{versionNumber} ({chunkCount} chunks, {extractedEntityCount} entities, {extractedRelationshipCount} relationships)",
            Payload = payload
        }, ct);
    }

    public Task RecordDocumentDeletedAsync(
        Guid documentId,
        string fileName,
        string scopeKey,
        int contributionsProcessed,
        string? actorId = null,
        string? actorName = null,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            fileName,
            contributionsProcessed
        }, JsonOptions);

        return RecordAsync(new GraphRagAuditEntry
        {
            ActionType = "DocumentDeleted",
            ActorKind = actorId is null ? "Service" : "User",
            ActorId = actorId,
            ActorName = actorName,
            SubjectKind = "Document",
            SubjectId = documentId.ToString(),
            ScopeKey = scopeKey,
            Summary = $"Deleted '{fileName}' ({contributionsProcessed} contributions processed)",
            Payload = payload
        }, ct);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
