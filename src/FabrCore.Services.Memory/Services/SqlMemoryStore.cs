using System.Text.Json;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// SQL Server-backed memory store using the mem schema.
/// Knowledge graph with three tables:
///   MemoryEntity (NODE) — concept nodes
///   MemoryChunk — content + embeddings (1+ per entity)
///   MemoryRelationship (EDGE) — typed edges between nodes
/// </summary>
internal class SqlMemoryStore : IMemoryStore
{
    private const string SchemaName = MemorySchemaInitializer.SchemaName;
    internal const string IndexSentinelName = "__MEMORY_INDEX__";
    private const string IndexEntityType = "MemoryIndex";
    private const string MemoryVersionKey = "__memoryVersion";
    private const string MemoryVersionValue = "4";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _connectionString;
    private readonly int _embeddingDimensions;
    private readonly IEmbeddings? _embeddings;
    private readonly ILogger<SqlMemoryStore> _logger;

    public SqlMemoryStore(
        AgentMemoryOptions options,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IEmbeddings? embeddings = null)
    {
        _logger = loggerFactory.CreateLogger<SqlMemoryStore>();
        _embeddings = embeddings;
        _embeddingDimensions = options.EmbeddingDimensions;

        _connectionString = string.IsNullOrWhiteSpace(options.ConnectionStringName)
            ? ""
            : configuration.GetConnectionString(options.ConnectionStringName) ?? "";

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning("Agent memory store has no valid connection string — memory operations will fail until configured");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Entity (Node) Operations
    // ═══════════════════════════════════════════════════════════════════

    public async Task<MemoryEntry> InsertEntityAsync(string scopeKey, MemoryEntry entry, CancellationToken ct = default)
    {
        entry.Metadata ??= [];
        entry.Metadata[MemoryVersionKey] = MemoryVersionValue;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            INSERT INTO {SchemaName}.MemoryEntity
                (EntityId, ScopeKey, Name, EntityType, Description, Visibility, IsPointInTime, Metadata)
            OUTPUT INSERTED.EntityId, INSERTED.CreatedAt, INSERTED.UpdatedAt
            VALUES (NEWID(), @scopeKey, @name, @entityType, @description, @visibility, @isPointInTime, @metadata);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@name", entry.Title);
        cmd.Parameters.AddWithValue("@entityType", entry.Type.ToString());
        cmd.Parameters.AddWithValue("@description", (object?)entry.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@visibility", entry.Temperature.ToString());
        cmd.Parameters.AddWithValue("@isPointInTime", entry.IsPointInTime);
        cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entry.Metadata, JsonOptions));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            entry.Id = reader.GetGuid(0);
            entry.CreatedAt = reader.GetDateTime(1);
            entry.UpdatedAt = reader.GetDateTime(2);
        }

        entry.ScopeKey = scopeKey;

        _logger.LogInformation("Inserted entity '{Title}' ({Type}) for agent '{Agent}' with ID {Id}",
            entry.Title, entry.Type, scopeKey, entry.Id);

        return entry;
    }

    public async Task<MemoryEntry?> GetEntityByIdAsync(string scopeKey, Guid entityId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT EntityId, ScopeKey, Name, EntityType, Description, Visibility, IsPointInTime, Metadata, CreatedAt, UpdatedAt
            FROM {SchemaName}.MemoryEntity
            WHERE ScopeKey = @scopeKey AND EntityId = @entityId
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@entityId", entityId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadEntityFromReader(reader);
    }

    public async Task<MemoryEntry> UpdateEntityAsync(string scopeKey, MemoryEntry entry, CancellationToken ct = default)
    {
        entry.Metadata ??= [];
        entry.Metadata[MemoryVersionKey] = MemoryVersionValue;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            UPDATE {SchemaName}.MemoryEntity
            SET Name = @name,
                EntityType = @entityType,
                Description = @description,
                Visibility = @visibility,
                IsPointInTime = @isPointInTime,
                Metadata = @metadata,
                UpdatedAt = SYSUTCDATETIME()
            OUTPUT INSERTED.UpdatedAt
            WHERE ScopeKey = @scopeKey AND EntityId = @entityId
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@entityId", entry.Id);
        cmd.Parameters.AddWithValue("@name", entry.Title);
        cmd.Parameters.AddWithValue("@entityType", entry.Type.ToString());
        cmd.Parameters.AddWithValue("@description", (object?)entry.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@visibility", entry.Temperature.ToString());
        cmd.Parameters.AddWithValue("@isPointInTime", entry.IsPointInTime);
        cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entry.Metadata, JsonOptions));

        var updatedAt = await cmd.ExecuteScalarAsync(ct);
        if (updatedAt is DateTime dt)
            entry.UpdatedAt = dt;

        _logger.LogInformation("Updated entity '{Title}' ({Id}) for agent '{Agent}'",
            entry.Title, entry.Id, scopeKey);

        return entry;
    }

    public async Task<bool> DeleteEntityAsync(string scopeKey, Guid entityId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Chunks, relationships, and the entity must go together — a mid-way failure
        // would otherwise orphan rows.
        await using var transaction = connection.BeginTransaction();
        int rows;
        try
        {
            var deleteChunksSql = $"DELETE FROM {SchemaName}.MemoryChunk WHERE EntityId = @entityId AND ScopeKey = @scopeKey";
            await using (var cmd = new SqlCommand(deleteChunksSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@entityId", entityId);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Delete relationships (both directions using MATCH syntax)
            var deleteRelsSql = $"""
                DELETE r
                FROM {SchemaName}.MemoryRelationship r, {SchemaName}.MemoryEntity e1, {SchemaName}.MemoryEntity e2
                WHERE MATCH(e1-(r)->e2)
                  AND (e1.EntityId = @entityId OR e2.EntityId = @entityId)
                  AND r.ScopeKey = @scopeKey
                """;
            await using (var cmd = new SqlCommand(deleteRelsSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@entityId", entityId);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var deleteEntitySql = $"DELETE FROM {SchemaName}.MemoryEntity WHERE EntityId = @entityId AND ScopeKey = @scopeKey";
            await using (var cmd = new SqlCommand(deleteEntitySql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@entityId", entityId);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation("Deleted entity {Id} in scope '{Scope}' (found={Found})", entityId, scopeKey, rows > 0);
        return rows > 0;
    }

    public async Task<IReadOnlyList<MemoryHeader>> GetHeadersAsync(
        string scopeKey, int limit, MemoryType? typeFilter = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT TOP(@limit) EntityId, Name, EntityType, Description, UpdatedAt, IsPointInTime
            FROM {SchemaName}.MemoryEntity
            WHERE ScopeKey = @scopeKey
              AND Name != '{IndexSentinelName}'
            """;

        if (typeFilter.HasValue)
            sql += "\n    AND EntityType = @entityType";

        sql += "\nORDER BY UpdatedAt DESC";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        if (typeFilter.HasValue)
            cmd.Parameters.AddWithValue("@entityType", typeFilter.Value.ToString());

        var headers = new List<MemoryHeader>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            headers.Add(new MemoryHeader
            {
                MemoryId = reader.GetGuid(reader.GetOrdinal("EntityId")),
                Title = reader.GetString(reader.GetOrdinal("Name")),
                Type = ParseMemoryType(reader.GetString(reader.GetOrdinal("EntityType"))),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
                IsPointInTime = reader.GetBoolean(reader.GetOrdinal("IsPointInTime"))
            });
        }

        return headers;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Chunk (Content + Embedding) Operations
    // ═══════════════════════════════════════════════════════════════════

    public async Task<MemoryChunkEntry> InsertChunkAsync(string scopeKey, MemoryChunkEntry chunk, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            INSERT INTO {SchemaName}.MemoryChunk
                (ChunkId, ScopeKey, EntityId, Content, Embedding, ChunkIndex, Metadata)
            OUTPUT INSERTED.ChunkId, INSERTED.CreatedAt, INSERTED.UpdatedAt
            VALUES (NEWID(), @scopeKey, @entityId, @content,
                    {(chunk.Embedding is not null ? $"CAST(@embedding AS VECTOR({_embeddingDimensions}))" : "NULL")},
                    @chunkIndex, @metadata);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@entityId", chunk.EntityId);
        cmd.Parameters.AddWithValue("@content", chunk.Content);
        cmd.Parameters.AddWithValue("@chunkIndex", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("@metadata", (object?)SerializeMetadata(chunk.Metadata) ?? DBNull.Value);

        if (chunk.Embedding is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@embedding", SqlDbTypeExtensions.Vector)
            {
                Value = new SqlVector<float>(chunk.Embedding)
            });
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            chunk.ChunkId = reader.GetGuid(0);
            chunk.CreatedAt = reader.GetDateTime(1);
            chunk.UpdatedAt = reader.GetDateTime(2);
        }

        _logger.LogDebug("Inserted chunk {ChunkId} for entity {EntityId} (index={Index})",
            chunk.ChunkId, chunk.EntityId, chunk.ChunkIndex);

        return chunk;
    }

    public async Task<MemoryChunkEntry> UpdateChunkAsync(string scopeKey, MemoryChunkEntry chunk, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            UPDATE {SchemaName}.MemoryChunk
            SET Content = @content,
                {(chunk.Embedding is not null ? $"Embedding = CAST(@embedding AS VECTOR({_embeddingDimensions}))," : "")}
                Metadata = @metadata,
                UpdatedAt = SYSUTCDATETIME()
            OUTPUT INSERTED.UpdatedAt
            WHERE ChunkId = @chunkId AND ScopeKey = @scopeKey
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@chunkId", chunk.ChunkId);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@content", chunk.Content);
        cmd.Parameters.AddWithValue("@metadata", (object?)SerializeMetadata(chunk.Metadata) ?? DBNull.Value);

        if (chunk.Embedding is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@embedding", SqlDbTypeExtensions.Vector)
            {
                Value = new SqlVector<float>(chunk.Embedding)
            });
        }

        var updatedAt = await cmd.ExecuteScalarAsync(ct);
        if (updatedAt is DateTime dt)
            chunk.UpdatedAt = dt;

        _logger.LogDebug("Updated chunk {ChunkId} for entity {EntityId}", chunk.ChunkId, chunk.EntityId);

        return chunk;
    }

    public async Task<MemoryChunkEntry?> GetPrimaryChunkAsync(string scopeKey, Guid entityId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT TOP(1) ChunkId, EntityId, Content, ChunkIndex, Metadata, CreatedAt, UpdatedAt
            FROM {SchemaName}.MemoryChunk
            WHERE ScopeKey = @scopeKey AND EntityId = @entityId
            ORDER BY ChunkIndex ASC
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@entityId", entityId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadChunkFromReader(reader);
    }

    public async Task<IReadOnlyList<MemoryChunkEntry>> GetChunksAsync(string scopeKey, Guid entityId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT ChunkId, EntityId, Content, ChunkIndex, Metadata, CreatedAt, UpdatedAt
            FROM {SchemaName}.MemoryChunk
            WHERE ScopeKey = @scopeKey AND EntityId = @entityId
            ORDER BY ChunkIndex ASC
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@entityId", entityId);

        var chunks = new List<MemoryChunkEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            chunks.Add(ReadChunkFromReader(reader));
        }

        return chunks;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Relationship (Edge) Operations
    // ═══════════════════════════════════════════════════════════════════

    public async Task InsertRelationshipAsync(
        string scopeKey, Guid fromEntityId, Guid toEntityId,
        string relationshipType, string? description = null, double weight = 1.0,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            INSERT INTO {SchemaName}.MemoryRelationship
                ($from_id, $to_id, ScopeKey, RelationshipType, Description, Weight, Metadata)
            VALUES (
                (SELECT $node_id FROM {SchemaName}.MemoryEntity WHERE EntityId = @fromId AND ScopeKey = @scopeKey),
                (SELECT $node_id FROM {SchemaName}.MemoryEntity WHERE EntityId = @toId AND ScopeKey = @scopeKey),
                @scopeKey, @relType, @description, @weight, @metadata
            );
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fromId", fromEntityId);
        cmd.Parameters.AddWithValue("@toId", toEntityId);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@relType", relationshipType);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@weight", weight);
        cmd.Parameters.AddWithValue("@metadata", (object?)SerializeMetadata(metadata) ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Created relationship {Type}: {From} → {To} for agent '{Agent}'",
            relationshipType, fromEntityId, toEntityId, scopeKey);
    }

    public async Task<IReadOnlyList<MemoryRelationshipEntry>> GetRelationshipsAsync(
        string scopeKey, Guid entityId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Get both outgoing and incoming relationships
        var sql = $"""
            SELECT
                r.RelationshipType, r.Description, r.Weight, r.Metadata, r.CreatedAt,
                e2.EntityId AS RelatedEntityId, e2.Name AS RelatedEntityName, e2.EntityType AS RelatedEntityType
            FROM {SchemaName}.MemoryRelationship r, {SchemaName}.MemoryEntity e1, {SchemaName}.MemoryEntity e2
            WHERE MATCH(e1-(r)->e2)
              AND e1.EntityId = @entityId
              AND r.ScopeKey = @scopeKey
            UNION ALL
            SELECT
                r.RelationshipType, r.Description, r.Weight, r.Metadata, r.CreatedAt,
                e1.EntityId AS RelatedEntityId, e1.Name AS RelatedEntityName, e1.EntityType AS RelatedEntityType
            FROM {SchemaName}.MemoryRelationship r, {SchemaName}.MemoryEntity e1, {SchemaName}.MemoryEntity e2
            WHERE MATCH(e1-(r)->e2)
              AND e2.EntityId = @entityId
              AND r.ScopeKey = @scopeKey
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@entityId", entityId);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

        var relationships = new List<MemoryRelationshipEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            relationships.Add(new MemoryRelationshipEntry
            {
                RelationshipType = reader.GetString(reader.GetOrdinal("RelationshipType")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                Weight = reader.GetDouble(reader.GetOrdinal("Weight")),
                Metadata = DeserializeMetadata(reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata"))),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                RelatedEntityId = reader.GetGuid(reader.GetOrdinal("RelatedEntityId")),
                RelatedEntityTitle = reader.GetString(reader.GetOrdinal("RelatedEntityName")),
                RelatedEntityType = ParseMemoryType(reader.GetString(reader.GetOrdinal("RelatedEntityType")))
            });
        }

        return relationships;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Search Operations
    // ═══════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<MemorySearchResult>> VectorSearchAsync(
        string scopeKey, float[] queryEmbedding, int limit,
        MemoryType? typeFilter = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT TOP(@limit)
                e.EntityId, e.ScopeKey, e.Name, e.EntityType, e.Description,
                e.Visibility, e.IsPointInTime, e.Metadata AS EntityMetadata,
                e.CreatedAt, e.UpdatedAt,
                c.Content,
                VECTOR_DISTANCE('cosine', c.Embedding, CAST(@queryVector AS VECTOR({_embeddingDimensions}))) AS Distance
            FROM {SchemaName}.MemoryChunk c
            INNER JOIN {SchemaName}.MemoryEntity e
                ON c.EntityId = e.EntityId AND c.ScopeKey = e.ScopeKey
            WHERE c.ScopeKey = @scopeKey
              AND c.Embedding IS NOT NULL
              AND e.Name != '{IndexSentinelName}'
            """;

        if (typeFilter.HasValue)
            sql += "\n    AND e.EntityType = @entityType";

        sql += "\nORDER BY Distance";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.Add(new SqlParameter("@queryVector", SqlDbTypeExtensions.Vector)
        {
            Value = new SqlVector<float>(queryEmbedding)
        });
        if (typeFilter.HasValue)
            cmd.Parameters.AddWithValue("@entityType", typeFilter.Value.ToString());

        var results = new List<MemorySearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = new MemoryEntry
            {
                Id = reader.GetGuid(reader.GetOrdinal("EntityId")),
                ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
                Title = reader.GetString(reader.GetOrdinal("Name")),
                Type = ParseMemoryType(reader.GetString(reader.GetOrdinal("EntityType"))),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                Content = reader.IsDBNull(reader.GetOrdinal("Content")) ? null : reader.GetString(reader.GetOrdinal("Content")),
                Temperature = ParseTemperature(reader.GetString(reader.GetOrdinal("Visibility"))),
                IsPointInTime = reader.GetBoolean(reader.GetOrdinal("IsPointInTime")),
                Metadata = DeserializeMetadata(reader.IsDBNull(reader.GetOrdinal("EntityMetadata")) ? null : reader.GetString(reader.GetOrdinal("EntityMetadata"))),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
            };

            results.Add(new MemorySearchResult
            {
                Entry = entry,
                Distance = reader.GetDouble(reader.GetOrdinal("Distance"))
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<(MemoryEntry Entity, MemoryChunkEntry Chunk, double Distance)>> FindSimilarByContentAsync(
        string scopeKey, float[] queryEmbedding, int limit, double maxDistance,
        CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT TOP(@limit)
                e.EntityId, e.ScopeKey, e.Name, e.EntityType, e.Description, e.Visibility,
                e.IsPointInTime, e.Metadata AS EntityMetadata, e.CreatedAt, e.UpdatedAt,
                c.ChunkId, c.Content, c.ChunkIndex, c.Metadata AS ChunkMetadata,
                c.CreatedAt AS ChunkCreatedAt, c.UpdatedAt AS ChunkUpdatedAt,
                VECTOR_DISTANCE('cosine', c.Embedding, CAST(@queryVector AS VECTOR({_embeddingDimensions}))) AS Distance
            FROM {SchemaName}.MemoryChunk c
            INNER JOIN {SchemaName}.MemoryEntity e
                ON c.EntityId = e.EntityId AND c.ScopeKey = e.ScopeKey
            WHERE c.ScopeKey = @scopeKey
              AND c.Embedding IS NOT NULL
              AND e.Name != '{IndexSentinelName}'
              AND VECTOR_DISTANCE('cosine', c.Embedding, CAST(@queryVector AS VECTOR({_embeddingDimensions}))) < @maxDistance
            ORDER BY Distance
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@maxDistance", maxDistance);
        cmd.Parameters.Add(new SqlParameter("@queryVector", SqlDbTypeExtensions.Vector)
        {
            Value = new SqlVector<float>(queryEmbedding)
        });

        var results = new List<(MemoryEntry, MemoryChunkEntry, double)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entity = new MemoryEntry
            {
                Id = reader.GetGuid(reader.GetOrdinal("EntityId")),
                ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
                Title = reader.GetString(reader.GetOrdinal("Name")),
                Type = ParseMemoryType(reader.GetString(reader.GetOrdinal("EntityType"))),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                Temperature = ParseTemperature(reader.GetString(reader.GetOrdinal("Visibility"))),
                IsPointInTime = reader.GetBoolean(reader.GetOrdinal("IsPointInTime")),
                Metadata = DeserializeMetadata(reader.IsDBNull(reader.GetOrdinal("EntityMetadata")) ? null : reader.GetString(reader.GetOrdinal("EntityMetadata"))),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
            };

            var chunk = new MemoryChunkEntry
            {
                ChunkId = reader.GetGuid(reader.GetOrdinal("ChunkId")),
                EntityId = entity.Id,
                Content = reader.GetString(reader.GetOrdinal("Content")),
                ChunkIndex = reader.GetInt32(reader.GetOrdinal("ChunkIndex")),
                Metadata = DeserializeMetadata(reader.IsDBNull(reader.GetOrdinal("ChunkMetadata")) ? null : reader.GetString(reader.GetOrdinal("ChunkMetadata"))),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("ChunkCreatedAt")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("ChunkUpdatedAt"))
            };

            results.Add((entity, chunk, reader.GetDouble(reader.GetOrdinal("Distance"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<(Guid Id1, Guid Id2, double Distance)>> FindDuplicatePairsAsync(
        string scopeKey, double distanceThreshold, MemoryType? typeFilter = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT
                c1.EntityId AS Id1, c2.EntityId AS Id2,
                VECTOR_DISTANCE('cosine', c1.Embedding, c2.Embedding) AS Distance
            FROM {SchemaName}.MemoryChunk c1
            INNER JOIN {SchemaName}.MemoryEntity e1 ON c1.EntityId = e1.EntityId AND c1.ScopeKey = e1.ScopeKey
            CROSS JOIN {SchemaName}.MemoryChunk c2
            INNER JOIN {SchemaName}.MemoryEntity e2 ON c2.EntityId = e2.EntityId AND c2.ScopeKey = e2.ScopeKey
            WHERE c1.ScopeKey = @scopeKey AND c2.ScopeKey = @scopeKey
              AND c1.EntityId < c2.EntityId
              AND c1.Embedding IS NOT NULL AND c2.Embedding IS NOT NULL
              AND e1.Name != '{IndexSentinelName}' AND e2.Name != '{IndexSentinelName}'
              AND e1.EntityType = e2.EntityType
              AND VECTOR_DISTANCE('cosine', c1.Embedding, c2.Embedding) < @threshold
            """;

        if (typeFilter.HasValue)
            sql += "\n    AND e1.EntityType = @entityType";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@threshold", distanceThreshold);
        if (typeFilter.HasValue)
            cmd.Parameters.AddWithValue("@entityType", typeFilter.Value.ToString());

        var pairs = new List<(Guid, Guid, double)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pairs.Add((
                reader.GetGuid(reader.GetOrdinal("Id1")),
                reader.GetGuid(reader.GetOrdinal("Id2")),
                reader.GetDouble(reader.GetOrdinal("Distance"))
            ));
        }

        return pairs;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Hot Index Operations
    // ═══════════════════════════════════════════════════════════════════

    public async Task<string?> GetIndexContentAsync(string scopeKey, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT Content FROM {SchemaName}.MemoryEntity
            WHERE ScopeKey = @scopeKey AND Name = '{IndexSentinelName}' AND EntityType = '{IndexEntityType}'
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : (string)result;
    }

    public async Task UpsertIndexContentAsync(string scopeKey, string indexJson, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await UpsertIndexContentCoreAsync(connection, null, scopeKey, indexJson, ct);
    }

    public async Task ModifyIndexContentAsync(
        string scopeKey, Func<string?, string?> transform, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Serialize read-modify-write per scope across processes: agents sharing a scope
        // (and admin edits) would otherwise lose index entries to concurrent writers.
        await using var transaction = connection.BeginTransaction();
        try
        {
            await using (var lockCmd = new SqlCommand(
                "EXEC sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;",
                connection, transaction))
            {
                lockCmd.Parameters.AddWithValue("@resource", $"mem-index-{scopeKey}");
                await lockCmd.ExecuteNonQueryAsync(ct);
            }

            string? currentJson;
            var selectSql = $"""
                SELECT Content FROM {SchemaName}.MemoryEntity
                WHERE ScopeKey = @scopeKey AND Name = '{IndexSentinelName}' AND EntityType = '{IndexEntityType}'
                """;
            await using (var selectCmd = new SqlCommand(selectSql, connection, transaction))
            {
                selectCmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                var result = await selectCmd.ExecuteScalarAsync(ct);
                currentJson = result is DBNull or null ? null : (string)result;
            }

            var newJson = transform(currentJson);
            if (newJson is not null && newJson != currentJson)
                await UpsertIndexContentCoreAsync(connection, transaction, scopeKey, newJson, ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task UpsertIndexContentCoreAsync(
        SqlConnection connection, SqlTransaction? transaction,
        string scopeKey, string indexJson, CancellationToken ct)
    {
        var metadataJson = JsonSerializer.Serialize(
            new Dictionary<string, string> { [MemoryVersionKey] = MemoryVersionValue }, JsonOptions);

        // MERGE with HOLDLOCK so two concurrent writers cannot both take the insert branch.
        var mergeSql = $"""
            MERGE {SchemaName}.MemoryEntity WITH (HOLDLOCK) AS target
            USING (SELECT @scopeKey AS ScopeKey) AS source
            ON target.ScopeKey = source.ScopeKey
               AND target.Name = '{IndexSentinelName}' AND target.EntityType = '{IndexEntityType}'
            WHEN MATCHED THEN
                UPDATE SET Content = @content, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (EntityId, ScopeKey, Name, EntityType, Description, Content, Visibility, Metadata)
                VALUES (NEWID(), @scopeKey, '{IndexSentinelName}', '{IndexEntityType}',
                        'Hot layer memory index', @content, 'Hot', @metadata);
            """;

        await using var cmd = new SqlCommand(mergeSql, connection, transaction);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@content", indexJson);
        cmd.Parameters.AddWithValue("@metadata", metadataJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Embedding Operations
    // ═══════════════════════════════════════════════════════════════════

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (_embeddings is null)
            throw new InvalidOperationException(
                "No IEmbeddings registered. Ensure AddFabrCoreServer() is configured " +
                "with an 'embeddings' model entry in fabrcore.json.");

        var result = await _embeddings.GetEmbeddings(text);
        return result.Vector.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static MemoryEntry ReadEntityFromReader(SqlDataReader reader)
    {
        var entityType = reader.GetString(reader.GetOrdinal("EntityType"));
        var visibility = reader.GetString(reader.GetOrdinal("Visibility"));
        var metadataJson = reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata"));

        return new MemoryEntry
        {
            Id = reader.GetGuid(reader.GetOrdinal("EntityId")),
            ScopeKey = reader.IsDBNull(reader.GetOrdinal("ScopeKey"))
                ? "" : reader.GetString(reader.GetOrdinal("ScopeKey")),
            Title = reader.GetString(reader.GetOrdinal("Name")),
            Type = ParseMemoryType(entityType),
            Temperature = ParseTemperature(visibility),
            Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
            IsPointInTime = reader.GetBoolean(reader.GetOrdinal("IsPointInTime")),
            Metadata = DeserializeMetadata(metadataJson),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
        };
    }

    private static MemoryChunkEntry ReadChunkFromReader(SqlDataReader reader)
    {
        return new MemoryChunkEntry
        {
            ChunkId = reader.GetGuid(reader.GetOrdinal("ChunkId")),
            EntityId = reader.GetGuid(reader.GetOrdinal("EntityId")),
            Content = reader.GetString(reader.GetOrdinal("Content")),
            ChunkIndex = reader.GetInt32(reader.GetOrdinal("ChunkIndex")),
            Metadata = DeserializeMetadata(reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata"))),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
        };
    }

    private static MemoryType ParseMemoryType(string entityType) =>
        Enum.TryParse<MemoryType>(entityType, ignoreCase: true, out var type)
            ? type
            : MemoryType.Observation;

    private static MemoryTemperature ParseTemperature(string visibility) =>
        visibility switch
        {
            "Hot" => MemoryTemperature.Hot,
            "Cold" => MemoryTemperature.Cold,
            _ => MemoryTemperature.Warm
        };

    private static string? SerializeMetadata(Dictionary<string, string>? metadata) =>
        metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions);

    private static Dictionary<string, string>? DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
