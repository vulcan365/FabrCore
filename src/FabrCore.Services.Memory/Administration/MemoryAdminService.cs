using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Administration.Models;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Administration;

/// <summary>
/// Default <see cref="IMemoryAdminService"/>. Reads query the <c>mem</c> schema with
/// parameterized SQL; mutations route through <see cref="IAgentMemoryProvider"/> /
/// <see cref="IMemoryScopeService"/> so the hot index, embeddings, and audit stay consistent.
/// </summary>
internal sealed class MemoryAdminService : IMemoryAdminService
{
    private const string Schema = MemorySchemaInitializer.SchemaName;
    private const string IndexSentinel = SqlMemoryStore.IndexSentinelName;

    private readonly IConfiguration _configuration;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MemoryAdminService> _logger;

    public MemoryAdminService(
        IConfiguration configuration,
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILogger<MemoryAdminService> logger)
    {
        _configuration = configuration;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private IAgentMemoryProvider MemoryProvider =>
        _serviceProvider.GetRequiredService<IAgentMemoryProvider>();

    private IMemoryScopeService ScopeService =>
        _serviceProvider.GetRequiredService<IMemoryScopeService>();

    private IMemoryAuditLog AuditLog =>
        _serviceProvider.GetRequiredService<IMemoryAuditLog>();

    private SqlConnection CreateConnection()
    {
        var connectionString = _configuration.GetConnectionString(_options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{_options.ConnectionStringName}' not found in configuration.");
        return new SqlConnection(connectionString);
    }

    // ─── Dashboard ──────────────────────────────────────────────────────

    public async Task<AdminMemoryDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        var stats = new AdminMemoryDashboardStats();

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT
                (SELECT COUNT(*) FROM {Schema}.MemoryScope) AS RegisteredScopes,
                (SELECT COUNT(DISTINCT ScopeKey) FROM {Schema}.MemoryEntity) AS EntityScopes,
                (SELECT COUNT(*) FROM {Schema}.MemoryEntity WHERE Name <> @sentinel) AS Memories,
                (SELECT COUNT(*) FROM {Schema}.MemoryChunk) AS Chunks,
                (SELECT COUNT(*) FROM {Schema}.MemoryRelationship) AS Relationships,
                (SELECT COUNT(*) FROM {Schema}.MemorySummaryNode) AS SummaryNodes;
            """;

        await using (var cmd = new SqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                stats.TotalScopes = Math.Max(reader.GetInt32(0), reader.GetInt32(1));
                stats.TotalMemories = reader.GetInt32(2);
                stats.TotalChunks = reader.GetInt32(3);
                stats.TotalRelationships = reader.GetInt32(4);
                stats.TotalSummaryNodes = reader.GetInt32(5);
            }
        }

        var byTypeSql = $"""
            SELECT EntityType, COUNT(*) FROM {Schema}.MemoryEntity
            WHERE Name <> @sentinel GROUP BY EntityType;
            """;
        await using (var cmd = new SqlCommand(byTypeSql, connection))
        {
            cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                stats.MemoriesByType[reader.GetString(0)] = reader.GetInt32(1);
        }

        var byTempSql = $"""
            SELECT Visibility, COUNT(*) FROM {Schema}.MemoryEntity
            WHERE Name <> @sentinel GROUP BY Visibility;
            """;
        await using (var cmd = new SqlCommand(byTempSql, connection))
        {
            cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                stats.MemoriesByTemperature[reader.GetString(0)] = reader.GetInt32(1);
        }

        stats.RecentActivity = (await ListAuditEntriesAsync(scopeKey: null, page: 1, pageSize: 10, ct)).ToList();
        return stats;
    }

    // ─── Scopes ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminMemoryScopeDto>> ListScopesAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = $"""
            WITH EntityAgg AS (
                SELECT ScopeKey,
                       SUM(CASE WHEN Name <> @sentinel THEN 1 ELSE 0 END) AS MemoryCount,
                       MAX(UpdatedAt) AS LastUpdatedAt
                FROM {Schema}.MemoryEntity
                GROUP BY ScopeKey
            )
            SELECT s.ScopeKey, s.Description, s.IsShared, CAST(1 AS BIT) AS IsRegistered,
                   s.CreatedAt, s.CreatedBy,
                   ISNULL(e.MemoryCount, 0) AS MemoryCount, e.LastUpdatedAt
            FROM {Schema}.MemoryScope s
            LEFT JOIN EntityAgg e ON e.ScopeKey = s.ScopeKey
            UNION ALL
            SELECT e.ScopeKey, NULL, CAST(0 AS BIT), CAST(0 AS BIT),
                   NULL, NULL, e.MemoryCount, e.LastUpdatedAt
            FROM EntityAgg e
            WHERE NOT EXISTS (SELECT 1 FROM {Schema}.MemoryScope s WHERE s.ScopeKey = e.ScopeKey)
            ORDER BY ScopeKey;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);

        var scopes = new List<AdminMemoryScopeDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scopes.Add(new AdminMemoryScopeDto
            {
                ScopeKey = reader.GetString(0),
                Description = reader.IsDBNull(1) ? null : reader.GetString(1),
                IsShared = reader.GetBoolean(2),
                IsRegistered = reader.GetBoolean(3),
                CreatedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                CreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                MemoryCount = reader.GetInt32(6),
                LastUpdatedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7)
            });
        }

        return scopes;
    }

    public async Task<AdminMemoryScopeDto> CreateSharedScopeAsync(
        string scopeKey, string? description, string? actorId = null, CancellationToken ct = default)
    {
        var scope = await ScopeService.CreateScopeAsync(
            scopeKey, description, isShared: true, createdBy: actorId, ct);

        return new AdminMemoryScopeDto
        {
            ScopeKey = scope.ScopeKey,
            Description = scope.Description,
            IsShared = scope.IsShared,
            IsRegistered = true,
            CreatedAt = scope.CreatedAt,
            CreatedBy = scope.CreatedBy,
            MemoryCount = 0
        };
    }

    public async Task<AdminScopeDeleteResult> DeleteScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

        var result = new AdminScopeDeleteResult { ScopeKey = scopeKey };

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();

        try
        {
            async Task<int> ExecAsync(string sql)
            {
                await using var cmd = new SqlCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);
                return await cmd.ExecuteNonQueryAsync(ct);
            }

            result.RelationshipsDeleted = await ExecAsync(
                $"DELETE FROM {Schema}.MemoryRelationship WHERE ScopeKey = @scopeKey");
            result.ChunksDeleted = await ExecAsync(
                $"DELETE FROM {Schema}.MemoryChunk WHERE ScopeKey = @scopeKey");
            result.SummaryNodesDeleted = await ExecAsync(
                $"DELETE FROM {Schema}.MemorySummaryNode WHERE ScopeKey = @scopeKey");
            result.MemoriesDeleted = await ExecAsync(
                $"DELETE FROM {Schema}.MemoryEntity WHERE ScopeKey = @scopeKey AND Name <> @sentinel");
            await ExecAsync(
                $"DELETE FROM {Schema}.MemoryEntity WHERE ScopeKey = @scopeKey"); // index sentinel
            await ExecAsync(
                $"DELETE FROM {Schema}.MemoryScope WHERE ScopeKey = @scopeKey");

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        // The cached service instance holds per-scope state (scope-registered flag,
        // consolidation gate) — drop it so a recreated scope starts clean.
        MemoryProvider.EvictMemoryService(scopeKey);

        await AuditLog.RecordAsync("ScopeDeleted", scopeKey,
            summary: $"{result.MemoriesDeleted} memories, {result.ChunksDeleted} chunks, " +
                     $"{result.RelationshipsDeleted} relationships deleted",
            actorId: actorId, ct: ct);

        _logger.LogWarning("Memory scope '{ScopeKey}' deleted by '{Actor}': {Memories} memories removed",
            scopeKey, actorId ?? "unknown", result.MemoriesDeleted);

        return result;
    }

    // ─── Memories ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminMemoryDto>> ListMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        int page = 1, int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT e.EntityId, e.ScopeKey, e.Name, e.EntityType, e.Visibility, e.IsPointInTime,
                   e.Description, e.CreatedAt, e.UpdatedAt,
                   (SELECT COUNT(*) FROM {Schema}.MemoryChunk c WHERE c.EntityId = e.EntityId) AS ChunkCount
            FROM {Schema}.MemoryEntity e
            WHERE {BuildMemoryFilterWhere(typeFilter, temperatureFilter, searchTerm)}
            ORDER BY e.UpdatedAt DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        AddMemoryFilterParameters(cmd, scopeKey, typeFilter, temperatureFilter, searchTerm);
        cmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);

        var memories = new List<AdminMemoryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            memories.Add(new AdminMemoryDto
            {
                MemoryId = reader.GetGuid(0),
                ScopeKey = reader.GetString(1),
                Title = reader.GetString(2),
                Type = ParseType(reader.GetString(3)),
                Temperature = ParseTemperature(reader.GetString(4)),
                IsPointInTime = reader.GetBoolean(5),
                Description = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8),
                ChunkCount = reader.GetInt32(9)
            });
        }

        return memories;
    }

    public async Task<int> CountMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT COUNT(*) FROM {Schema}.MemoryEntity e
            WHERE {BuildMemoryFilterWhere(typeFilter, temperatureFilter, searchTerm)};
            """;

        await using var cmd = new SqlCommand(sql, connection);
        AddMemoryFilterParameters(cmd, scopeKey, typeFilter, temperatureFilter, searchTerm);
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    private string BuildMemoryFilterWhere(
        MemoryType? typeFilter, MemoryTemperature? temperatureFilter, string? searchTerm)
    {
        var where = "e.ScopeKey = @scopeKey AND e.Name <> @sentinel";
        if (typeFilter is not null)
            where += " AND e.EntityType = @entityType";
        if (temperatureFilter is not null)
            where += " AND e.Visibility = @visibility";
        if (!string.IsNullOrWhiteSpace(searchTerm))
            where += $"""
                 AND (e.Name LIKE @search ESCAPE '\'
                      OR e.Description LIKE @search ESCAPE '\'
                      OR EXISTS (SELECT 1 FROM {Schema}.MemoryChunk c
                                 WHERE c.EntityId = e.EntityId AND c.Content LIKE @search ESCAPE '\'))
                """;
        return where;
    }

    private static void AddMemoryFilterParameters(
        SqlCommand cmd, string scopeKey,
        MemoryType? typeFilter, MemoryTemperature? temperatureFilter, string? searchTerm)
    {
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);
        if (typeFilter is not null)
            cmd.Parameters.AddWithValue("@entityType", typeFilter.Value.ToString());
        if (temperatureFilter is not null)
            cmd.Parameters.AddWithValue("@visibility", temperatureFilter.Value.ToString());
        if (!string.IsNullOrWhiteSpace(searchTerm))
            cmd.Parameters.AddWithValue("@search", $"%{EscapeLike(searchTerm)}%");
    }

    private static string EscapeLike(string term) =>
        term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");

    public async Task<AdminMemoryDetailDto?> GetMemoryAsync(Guid memoryId, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        AdminMemoryDetailDto? detail = null;

        var entitySql = $"""
            SELECT EntityId, ScopeKey, Name, EntityType, Visibility, IsPointInTime,
                   Description, Metadata, CreatedAt, UpdatedAt
            FROM {Schema}.MemoryEntity
            WHERE EntityId = @memoryId AND Name <> @sentinel;
            """;
        await using (var cmd = new SqlCommand(entitySql, connection))
        {
            cmd.Parameters.AddWithValue("@memoryId", memoryId);
            cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            detail = new AdminMemoryDetailDto
            {
                MemoryId = reader.GetGuid(0),
                ScopeKey = reader.GetString(1),
                Title = reader.GetString(2),
                Type = ParseType(reader.GetString(3)),
                Temperature = ParseTemperature(reader.GetString(4)),
                IsPointInTime = reader.GetBoolean(5),
                Description = reader.IsDBNull(6) ? null : reader.GetString(6),
                Metadata = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetDateTime(8),
                UpdatedAt = reader.GetDateTime(9)
            };
        }

        var chunksSql = $"""
            SELECT ChunkId, ChunkIndex, Content,
                   CASE WHEN Embedding IS NULL THEN 0 ELSE 1 END AS HasEmbedding
            FROM {Schema}.MemoryChunk
            WHERE EntityId = @memoryId
            ORDER BY ChunkIndex;
            """;
        await using (var cmd = new SqlCommand(chunksSql, connection))
        {
            cmd.Parameters.AddWithValue("@memoryId", memoryId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                detail.Chunks.Add(new AdminMemoryChunkDto
                {
                    ChunkId = reader.GetGuid(0),
                    ChunkIndex = reader.GetInt32(1),
                    Content = reader.GetString(2),
                    HasEmbedding = reader.GetInt32(3) == 1
                });
            }
        }

        detail.Content = detail.Chunks.FirstOrDefault(c => c.ChunkIndex == 0)?.Content
            ?? detail.Chunks.FirstOrDefault()?.Content;

        var relsSql = $"""
            SELECT e2.EntityId, e2.Name, r.RelationshipType, 'outgoing' AS Direction
            FROM {Schema}.MemoryRelationship r, {Schema}.MemoryEntity e1, {Schema}.MemoryEntity e2
            WHERE MATCH(e1-(r)->e2) AND e1.EntityId = @memoryId
            UNION ALL
            SELECT e1.EntityId, e1.Name, r.RelationshipType, 'incoming' AS Direction
            FROM {Schema}.MemoryRelationship r, {Schema}.MemoryEntity e1, {Schema}.MemoryEntity e2
            WHERE MATCH(e1-(r)->e2) AND e2.EntityId = @memoryId;
            """;
        await using (var cmd = new SqlCommand(relsSql, connection))
        {
            cmd.Parameters.AddWithValue("@memoryId", memoryId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                detail.Relationships.Add(new AdminMemoryRelationshipDto
                {
                    RelatedMemoryId = reader.GetGuid(0),
                    RelatedTitle = reader.GetString(1),
                    RelationshipType = reader.GetString(2),
                    Direction = reader.GetString(3)
                });
            }
        }

        return detail;
    }

    public async Task<AdminMemoryDto> CreateMemoryAsync(
        string scopeKey, string title, MemoryType type, string content,
        string? description = null,
        MemoryTemperature temperature = MemoryTemperature.Warm,
        bool isPointInTime = false,
        Dictionary<string, string>? metadata = null,
        string? actorId = null,
        CancellationToken ct = default)
    {
        var service = MemoryProvider.GetMemoryService(scopeKey);
        var entry = await service.SaveMemoryAsync(title, type, content, description, metadata, isPointInTime, ct);

        if (temperature != entry.Temperature)
            entry = await service.UpdateMemoryAsync(entry.Id, temperature: temperature, ct: ct);

        await AuditLog.RecordAsync("AdminCreated", scopeKey, entry.Id,
            summary: title, actorId: actorId, ct: ct);

        return new AdminMemoryDto
        {
            MemoryId = entry.Id,
            ScopeKey = scopeKey,
            Title = entry.Title,
            Type = entry.Type,
            Temperature = entry.Temperature,
            IsPointInTime = entry.IsPointInTime,
            Description = entry.Description,
            ChunkCount = 1,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt
        };
    }

    public async Task<AdminMemoryDetailDto> UpdateMemoryAsync(
        Guid memoryId, string title, MemoryType type, string content,
        string? description, MemoryTemperature temperature,
        string? actorId = null,
        CancellationToken ct = default)
    {
        var scopeKey = await GetScopeKeyForMemoryAsync(memoryId, ct)
            ?? throw new InvalidOperationException($"Memory {memoryId} not found.");

        var service = MemoryProvider.GetMemoryService(scopeKey);
        await service.UpdateMemoryAsync(memoryId, title, type, content, description, temperature, ct);

        await AuditLog.RecordAsync("AdminUpdated", scopeKey, memoryId,
            summary: title, actorId: actorId, ct: ct);

        return await GetMemoryAsync(memoryId, ct)
            ?? throw new InvalidOperationException($"Memory {memoryId} disappeared during update.");
    }

    public async Task<bool> DeleteMemoryAsync(Guid memoryId, string? actorId = null, CancellationToken ct = default)
    {
        var scopeKey = await GetScopeKeyForMemoryAsync(memoryId, ct);
        if (scopeKey is null)
            return false;

        var service = MemoryProvider.GetMemoryService(scopeKey);
        var deleted = await service.ForgetMemoryAsync(memoryId, ct);

        if (deleted)
        {
            await AuditLog.RecordAsync("AdminDeleted", scopeKey, memoryId, actorId: actorId, ct: ct);
        }

        return deleted;
    }

    // ─── Maintenance ────────────────────────────────────────────────────

    public async Task<MemoryConsolidationResult> ConsolidateScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default)
    {
        var service = MemoryProvider.GetMemoryService(scopeKey);
        return await service.ConsolidateAsync(ct);
    }

    // ─── Audit ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MemoryAuditEntry>> ListAuditEntriesAsync(
        string? scopeKey = null, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT AuditId, OccurredAt, ActionType, ScopeKey, MemoryId,
                   ActorId, ActorName, Summary, Payload, DurationMs
            FROM {Schema}.MemoryAuditLog
            {(scopeKey is null ? "" : "WHERE ScopeKey = @scopeKey")}
            ORDER BY OccurredAt DESC, AuditId DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        if (scopeKey is not null)
            cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);

        var entries = new List<MemoryAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new MemoryAuditEntry
            {
                AuditId = reader.GetInt64(0),
                OccurredAt = reader.GetDateTime(1),
                ActionType = reader.GetString(2),
                ScopeKey = reader.GetString(3),
                MemoryId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                ActorId = reader.IsDBNull(5) ? null : reader.GetString(5),
                ActorName = reader.IsDBNull(6) ? null : reader.GetString(6),
                Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
                Payload = reader.IsDBNull(8) ? null : reader.GetString(8),
                DurationMs = reader.IsDBNull(9) ? null : reader.GetInt64(9)
            });
        }

        return entries;
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private async Task<string?> GetScopeKeyForMemoryAsync(Guid memoryId, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = $"SELECT ScopeKey FROM {Schema}.MemoryEntity WHERE EntityId = @memoryId AND Name <> @sentinel;";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@memoryId", memoryId);
        cmd.Parameters.AddWithValue("@sentinel", IndexSentinel);

        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static MemoryType ParseType(string value) =>
        Enum.TryParse<MemoryType>(value, ignoreCase: true, out var type) ? type : MemoryType.Observation;

    private static MemoryTemperature ParseTemperature(string value) =>
        Enum.TryParse<MemoryTemperature>(value, ignoreCase: true, out var temp) ? temp : MemoryTemperature.Warm;
}
