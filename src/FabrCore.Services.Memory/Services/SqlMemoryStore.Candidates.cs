using System.Text.Json;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Models;
using Microsoft.Data.SqlClient;

namespace FabrCore.Services.Memory.Services;

internal partial class SqlMemoryStore : IMemoryCandidateStore
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<MemoryChunkEvidence>>> GetMatchedChunksAsync(string scopeKey,
        float[] embedding, IReadOnlyCollection<Guid> ids, int chunksPerMemory, int charactersPerChunk, CancellationToken ct = default)
    {
        if (chunksPerMemory is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(chunksPerMemory));
        if (charactersPerChunk is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(charactersPerChunk));
        if (ids.Count > 200) throw new ArgumentOutOfRangeException(nameof(ids));
        var result = new Dictionary<Guid, List<MemoryChunkEvidence>>();
        if (ids.Count == 0) return new Dictionary<Guid, IReadOnlyList<MemoryChunkEvidence>>();
        await using var lease = await OpenConnectionAsync(ct);
        var sql = $"""
            SELECT e.EntityId, best.ChunkId, best.ChunkIndex, LEFT(best.Content, @length),
                CASE WHEN DATALENGTH(best.Content)/2 > @length THEN 1 ELSE 0 END
            FROM {SchemaName}.MemoryEntity e
            CROSS APPLY (SELECT TOP(@count) c.ChunkId, c.ChunkIndex, c.Content,
                VECTOR_DISTANCE('cosine', c.Embedding, CAST(@vector AS VECTOR({_embeddingDimensions}))) AS Distance
                FROM {SchemaName}.MemoryChunk c
                WHERE c.ScopeKey=e.ScopeKey AND c.EntityId=e.EntityId AND c.Embedding IS NOT NULL
                ORDER BY Distance, c.ChunkIndex, c.ChunkId) best
            WHERE e.ScopeKey=@scope AND e.Visibility!='Cold' AND e.Name!='{IndexSentinelName}'
                AND e.EntityId IN (SELECT TRY_CONVERT(uniqueidentifier, value) FROM OPENJSON(@ids))
            ORDER BY e.EntityId, best.Distance, best.ChunkIndex, best.ChunkId
            """;
        await using var cmd = new SqlCommand(sql, lease.Connection, ActiveTransaction);
        cmd.Parameters.AddWithValue("@scope", scopeKey);
        cmd.Parameters.AddWithValue("@length", charactersPerChunk);
        cmd.Parameters.AddWithValue("@count", chunksPerMemory);
        cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(ids));
        cmd.Parameters.AddWithValue("@vector", JsonSerializer.Serialize(embedding));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            if (!result.TryGetValue(id, out var chunks)) result[id] = chunks = [];
            chunks.Add(new(reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4) != 0));
        }
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<MemoryChunkEvidence>)p.Value);
    }

    public async Task<IReadOnlyDictionary<Guid, MemoryChunkEvidence>> GetMatchedChunkEvidenceAsync(string scopeKey,
        float[] embedding, IReadOnlyCollection<Guid> ids, int charactersPerMemory, CancellationToken ct = default)
    {
        if (charactersPerMemory is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(charactersPerMemory));
        if (ids.Count > 200) throw new ArgumentOutOfRangeException(nameof(ids));
        var result = new Dictionary<Guid, MemoryChunkEvidence>();
        if (ids.Count == 0) return result;
        await using var lease = await OpenConnectionAsync(ct);
        var sql = $"""
            SELECT e.EntityId, best.ChunkId, best.ChunkIndex, LEFT(best.Content, @length),
                CASE WHEN DATALENGTH(best.Content)/2 > @length THEN 1 ELSE 0 END
            FROM {SchemaName}.MemoryEntity e
            CROSS APPLY (SELECT TOP(1) c.ChunkId, c.ChunkIndex, c.Content
                FROM {SchemaName}.MemoryChunk c
                WHERE c.ScopeKey=e.ScopeKey AND c.EntityId=e.EntityId AND c.Embedding IS NOT NULL
                ORDER BY VECTOR_DISTANCE('cosine', c.Embedding, CAST(@vector AS VECTOR({_embeddingDimensions}))),
                    c.ChunkIndex, c.ChunkId) best
            WHERE e.ScopeKey=@scope AND e.Visibility!='Cold' AND e.Name!='{IndexSentinelName}'
                AND e.EntityId IN (SELECT TRY_CONVERT(uniqueidentifier, value) FROM OPENJSON(@ids))
            """;
        await using var cmd = new SqlCommand(sql, lease.Connection, ActiveTransaction);
        cmd.Parameters.AddWithValue("@scope", scopeKey);
        cmd.Parameters.AddWithValue("@length", charactersPerMemory);
        cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(ids));
        cmd.Parameters.AddWithValue("@vector", JsonSerializer.Serialize(embedding));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0),
            new MemoryChunkEvidence(reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4) != 0));
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetCandidatePreviewsAsync(string scopeKey,
        IReadOnlyCollection<Guid> ids, int charactersPerMemory, CancellationToken ct = default)
    {
        if (charactersPerMemory is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(charactersPerMemory));
        if (ids.Count > 200) throw new ArgumentOutOfRangeException(nameof(ids), "At most 200 preview candidates are supported.");
        var result = new Dictionary<Guid, string>();
        if (ids.Count == 0) return result;
        await using var lease = await OpenConnectionAsync(ct);
        var sql = $"""
            SELECT e.EntityId, LEFT(c.Content, @length)
            FROM {SchemaName}.MemoryEntity e
            JOIN {SchemaName}.MemoryChunk c ON c.ScopeKey=e.ScopeKey AND c.EntityId=e.EntityId
            WHERE e.ScopeKey=@scope AND e.Visibility!='Cold' AND e.Name!='{IndexSentinelName}'
                AND c.ChunkIndex=0 AND e.EntityId IN
                (SELECT TRY_CONVERT(uniqueidentifier, value) FROM OPENJSON(@ids))
            """;
        await using var cmd = new SqlCommand(sql, lease.Connection, ActiveTransaction);
        cmd.Parameters.AddWithValue("@scope", scopeKey);
        cmd.Parameters.AddWithValue("@length", charactersPerMemory);
        cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(ids));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(1)) result.TryAdd(reader.GetGuid(0), reader.GetString(1));
        return result;
    }

    public Task<IReadOnlyList<MemoryHeader>> FindCandidateHeadersAsync(string scopeKey,
        float[] embedding, int limit, IReadOnlyCollection<Guid>? excludedIds = null, CancellationToken ct = default)
        => FindCandidateHeadersCoreAsync(scopeKey, embedding, limit, excludedIds, false, false, ct);

    public Task<IReadOnlyList<MemoryHeader>> FindDiverseCandidateHeadersAsync(string scopeKey,
        float[] embedding, int limit, IReadOnlyCollection<Guid>? excludedIds = null, CancellationToken ct = default)
        => FindCandidateHeadersCoreAsync(scopeKey, embedding, limit, excludedIds, true, false, ct);

    public Task<IReadOnlyList<MemoryHeader>> FindHybridCandidateHeadersAsync(string scopeKey,
        float[] embedding, int limit, IReadOnlyCollection<Guid>? excludedIds = null, CancellationToken ct = default)
        => FindCandidateHeadersCoreAsync(scopeKey, embedding, limit, excludedIds, false, true, ct);

    private async Task<IReadOnlyList<MemoryHeader>> FindCandidateHeadersCoreAsync(string scopeKey,
        float[] embedding, int limit, IReadOnlyCollection<Guid>? excludedIds, bool diverse, bool hybrid, CancellationToken ct)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var lease = await OpenConnectionAsync(ct);
        // Aggregate chunks before TOP; cold/excluded rows cannot consume the candidate budget.
        var sql = $"""
            WITH candidates AS (
            SELECT e.EntityId, e.Name, e.EntityType, e.Description, e.UpdatedAt, e.IsPointInTime,
                nearest.Distance,
                ROW_NUMBER() OVER (ORDER BY nearest.Distance, e.UpdatedAt DESC, e.EntityId) AS GlobalRank,
                ROW_NUMBER() OVER (PARTITION BY e.EntityType ORDER BY nearest.Distance, e.UpdatedAt DESC, e.EntityId) AS TypeRank
            FROM {SchemaName}.MemoryEntity e
            CROSS APPLY (
                SELECT MIN(VECTOR_DISTANCE('cosine', c.Embedding,
                    CAST(@vector AS VECTOR({_embeddingDimensions})))) AS Distance
                FROM {SchemaName}.MemoryChunk c
                WHERE c.ScopeKey = e.ScopeKey AND c.EntityId = e.EntityId AND c.Embedding IS NOT NULL
            ) nearest
            WHERE e.ScopeKey = @scope AND e.Visibility != 'Cold' AND e.Name != '{IndexSentinelName}'
                AND nearest.Distance IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM OPENJSON(@excluded) x WHERE TRY_CONVERT(uniqueidentifier, x.value) = e.EntityId)
            )
            SELECT TOP(@limit) EntityId, Name, EntityType, Description, UpdatedAt, IsPointInTime
            FROM candidates
            ORDER BY {(hybrid ? "CASE WHEN GlobalRank <= @reserved THEN 0 ELSE 1 END, CASE WHEN GlobalRank <= @reserved THEN 0 ELSE TypeRank END," : diverse ? "TypeRank," : "")} Distance, UpdatedAt DESC, EntityId
            """;
        await using var cmd = new SqlCommand(sql, lease.Connection, ActiveTransaction);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@reserved", Math.Min(limit, limit / 2 + 1));
        cmd.Parameters.AddWithValue("@scope", scopeKey);
        cmd.Parameters.AddWithValue("@vector", JsonSerializer.Serialize(embedding));
        cmd.Parameters.AddWithValue("@excluded", JsonSerializer.Serialize(excludedIds ?? Array.Empty<Guid>()));
        var result = new List<MemoryHeader>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new MemoryHeader
        {
            MemoryId = reader.GetGuid(0), Title = reader.GetString(1), Type = ParseMemoryType(reader.GetString(2)),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3), UpdatedAt = reader.GetDateTime(4),
            IsPointInTime = reader.GetBoolean(5)
        });
        return result;
    }
}
