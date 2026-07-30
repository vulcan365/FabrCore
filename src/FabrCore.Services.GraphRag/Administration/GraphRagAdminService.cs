using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.Administration.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Administration;

public sealed class GraphRagAdminService : IGraphRagAdminService
{
    private readonly string _connectionString;
    private readonly string? _hostApiBaseUrl;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GraphRagAdminService> _logger;
    private static readonly string Schema = GraphRagSchemaInitializer.SchemaName;

    internal GraphRagAdminService(
        IConfiguration configuration,
        GraphRagOptions options,
        IServiceProvider serviceProvider,
        ILogger<GraphRagAdminService> logger)
    {
        _connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' not found");
        _hostApiBaseUrl = configuration["FabrCoreHostUrl"];
        _httpClientFactory = serviceProvider.GetService(typeof(IHttpClientFactory)) as IHttpClientFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // ─── Dashboard ───────────────────────────────────────────────────────

    public async Task<AdminDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT
                (SELECT COUNT(*) FROM {Schema}.KnowledgeScope) AS TotalScopes,
                (SELECT COUNT(*) FROM {Schema}.KnowledgeEntity) AS TotalEntities,
                (SELECT COUNT(*) FROM {Schema}.KnowledgeRelationship) AS TotalRelationships,
                (SELECT COUNT(*) FROM {Schema}.KnowledgeChunk) AS TotalChunks,
                (SELECT COUNT(*) FROM {Schema}.KnowledgeDomain) AS TotalDomains,
                (SELECT COUNT(*) FROM {Schema}.KnowledgeCategory) AS TotalCategories
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new AdminDashboardStats
        {
            TotalScopes = reader.GetInt32(0),
            TotalEntities = reader.GetInt32(1),
            TotalRelationships = reader.GetInt32(2),
            TotalChunks = reader.GetInt32(3),
            TotalDomains = reader.GetInt32(4),
            TotalCategories = reader.GetInt32(5)
        };
    }

    // ─── Scopes ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminScopeDto>> ListScopesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT s.ScopeKey, s.Description, s.DefaultPriority, s.Metadata, s.CreatedAt,
                   ISNULL(ec.Cnt, 0) AS EntityCount
            FROM {Schema}.KnowledgeScope s
            LEFT JOIN (
                SELECT ScopeKey, COUNT(*) AS Cnt
                FROM {Schema}.KnowledgeEntity
                GROUP BY ScopeKey
            ) ec ON ec.ScopeKey = s.ScopeKey
            ORDER BY s.ScopeKey
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<AdminScopeDto>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadScopeDto(reader));
        return list;
    }

    public async Task<AdminScopeDto?> GetScopeAsync(string scopeKey, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT s.ScopeKey, s.Description, s.DefaultPriority, s.Metadata, s.CreatedAt,
                   (SELECT COUNT(*) FROM {Schema}.KnowledgeEntity WHERE ScopeKey = s.ScopeKey) AS EntityCount
            FROM {Schema}.KnowledgeScope s
            WHERE s.ScopeKey = @scopeKey
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadScopeDto(reader) : null;
    }

    public async Task<AdminScopeDto> CreateScopeAsync(string scopeKey, string? description, double defaultPriority = 1.0, string? metadata = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            INSERT INTO {Schema}.KnowledgeScope (ScopeKey, Description, DefaultPriority, Metadata)
            VALUES (@scopeKey, @description, @defaultPriority, @metadata);
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@defaultPriority", defaultPriority);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Admin: Created scope '{ScopeKey}'", scopeKey);
        return (await GetScopeAsync(scopeKey, ct))!;
    }

    public async Task<AdminScopeDto> UpdateScopeAsync(string scopeKey, string? description, double defaultPriority = 1.0, string? metadata = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            UPDATE {Schema}.KnowledgeScope
            SET Description = @description, DefaultPriority = @defaultPriority, Metadata = @metadata
            WHERE ScopeKey = @scopeKey
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@defaultPriority", defaultPriority);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Admin: Updated scope '{ScopeKey}'", scopeKey);
        return (await GetScopeAsync(scopeKey, ct))!;
    }

    // ─── Entities ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminEntityDto>> ListEntitiesAsync(
        string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var (where, parameters) = BuildEntityWhereClause(scopeFilter, entityTypeFilter, searchTerm);
        var offset = (page - 1) * pageSize;

        var sql = $"""
            SELECT e.EntityId, e.CanonicalEntityId, e.Name, e.EntityType, e.ScopeKey, e.Description, e.Metadata,
                   e.CreatedAt, e.UpdatedAt,
                   CASE WHEN e.Embedding IS NOT NULL THEN 1 ELSE 0 END AS HasEmbedding,
                   ISNULL(cc.Cnt, 0) AS ChunkCount,
                   d.Name AS DomainName, cat.Name AS CategoryName
            FROM {Schema}.KnowledgeEntity e
            LEFT JOIN (SELECT EntityId, COUNT(*) AS Cnt FROM {Schema}.KnowledgeChunk GROUP BY EntityId) cc ON cc.EntityId = e.EntityId
            LEFT JOIN {Schema}.BelongsTo bt_ec ON e.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = e.ScopeKey
            LEFT JOIN {Schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
            LEFT JOIN {Schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
            LEFT JOIN {Schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
            {where}
            ORDER BY e.Name
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@offset", offset);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AdminEntityDto>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadEntityDto(reader));
        return list;
    }

    public async Task<int> CountEntitiesAsync(string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var (where, parameters) = BuildEntityWhereClause(scopeFilter, entityTypeFilter, searchTerm);
        var sql = $"SELECT COUNT(*) FROM {Schema}.KnowledgeEntity e {where}";

        await using var cmd = new SqlCommand(sql, conn);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<AdminEntityDto?> GetEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT e.EntityId, e.CanonicalEntityId, e.Name, e.EntityType, e.ScopeKey, e.Description, e.Content, e.Metadata,
                   e.CreatedAt, e.UpdatedAt,
                   CASE WHEN e.Embedding IS NOT NULL THEN 1 ELSE 0 END AS HasEmbedding,
                   ISNULL(cc.Cnt, 0) AS ChunkCount,
                   d.Name AS DomainName, cat.Name AS CategoryName
            FROM {Schema}.KnowledgeEntity e
            LEFT JOIN (SELECT EntityId, COUNT(*) AS Cnt FROM {Schema}.KnowledgeChunk GROUP BY EntityId) cc ON cc.EntityId = e.EntityId
            LEFT JOIN {Schema}.BelongsTo bt_ec ON e.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = e.ScopeKey
            LEFT JOIN {Schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
            LEFT JOIN {Schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
            LEFT JOIN {Schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
            WHERE e.EntityId = @entityId
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@entityId", entityId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var dto = ReadEntityDto(reader);
        dto.Content = reader.IsDBNull(reader.GetOrdinal("Content")) ? null : reader.GetString(reader.GetOrdinal("Content"));
        return dto;
    }

    public async Task<AdminEntityDto> UpdateEntityAsync(Guid entityId, string? description, string? content, string? metadata, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            UPDATE {Schema}.KnowledgeEntity
            SET Description = @description, Content = @content, Metadata = @metadata, UpdatedAt = SYSUTCDATETIME()
            WHERE EntityId = @entityId
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@entityId", entityId);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@content", (object?)content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Admin: Updated entity {EntityId}", entityId);
        return (await GetEntityAsync(entityId, ct))!;
    }

    public async Task DeleteEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Cascade: chunks → BelongsTo edges → relationship edges → entity
        var sql = $"""
            DELETE FROM {Schema}.KnowledgeChunk WHERE EntityId = @entityId;

            DELETE bt FROM {Schema}.BelongsTo bt
            INNER JOIN {Schema}.KnowledgeEntity e ON e.$node_id = bt.$from_id
            WHERE e.EntityId = @entityId;

            DELETE r FROM {Schema}.KnowledgeRelationship r
            INNER JOIN {Schema}.KnowledgeEntity ef ON ef.$node_id = r.$from_id
            WHERE ef.EntityId = @entityId;

            DELETE r FROM {Schema}.KnowledgeRelationship r
            INNER JOIN {Schema}.KnowledgeEntity et ON et.$node_id = r.$to_id
            WHERE et.EntityId = @entityId;

            DELETE FROM {Schema}.KnowledgeEntity WHERE EntityId = @entityId;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@entityId", entityId);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Admin: Deleted entity {EntityId} and cascaded", entityId);
    }

    public async Task<IReadOnlyList<string>> ListEntityTypesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"SELECT DISTINCT EntityType FROM {Schema}.KnowledgeEntity ORDER BY EntityType";
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<string>();
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    // ─── Chunks ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminChunkDto>> ListChunksForEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT ChunkId, EntityId, ScopeKey, Content,
                   CASE WHEN Embedding IS NOT NULL THEN 1 ELSE 0 END AS HasEmbedding,
                   ChunkIndex, Metadata, CreatedAt
            FROM {Schema}.KnowledgeChunk
            WHERE EntityId = @entityId
            ORDER BY ChunkIndex
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@entityId", entityId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<AdminChunkDto>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AdminChunkDto
            {
                ChunkId = reader.GetGuid(reader.GetOrdinal("ChunkId")),
                EntityId = reader.GetGuid(reader.GetOrdinal("EntityId")),
                ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
                Content = reader.GetString(reader.GetOrdinal("Content")),
                HasEmbedding = reader.GetInt32(reader.GetOrdinal("HasEmbedding")) == 1,
                ChunkIndex = reader.GetInt32(reader.GetOrdinal("ChunkIndex")),
                Metadata = reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
            });
        }
        return list;
    }

    // ─── Relationships ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminRelationshipDto>> ListRelationshipsAsync(
        string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var (where, parameters) = BuildRelationshipWhereClause(scopeFilter, entityNameFilter, relationshipTypeFilter);
        var offset = (page - 1) * pageSize;

        var sql = $"""
            ;WITH RelData AS (
                SELECT r.RelationshipType, r.Description, r.Weight, r.Metadata, r.CreatedAt,
                       ef.Name AS FromEntityName, ef.EntityType AS FromEntityType,
                       et.Name AS ToEntityName, et.EntityType AS ToEntityType,
                       ef.ScopeKey
                FROM {Schema}.KnowledgeRelationship r,
                     {Schema}.KnowledgeEntity ef,
                     {Schema}.KnowledgeEntity et
                WHERE MATCH(ef-(r)->et)
                  AND r.ScopeKey = ef.ScopeKey AND et.ScopeKey = ef.ScopeKey
            )
            SELECT * FROM RelData
            {where}
            ORDER BY FromEntityName, ToEntityName
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@offset", offset);
        cmd.Parameters.AddWithValue("@pageSize", pageSize);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AdminRelationshipDto>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadRelationshipDto(reader));
        return list;
    }

    public async Task<int> CountRelationshipsAsync(string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var (where, parameters) = BuildRelationshipWhereClause(scopeFilter, entityNameFilter, relationshipTypeFilter);

        var sql = $"""
            ;WITH RelData AS (
                SELECT ef.Name AS FromEntityName, ef.ScopeKey, r.RelationshipType
                FROM {Schema}.KnowledgeRelationship r,
                     {Schema}.KnowledgeEntity ef,
                     {Schema}.KnowledgeEntity et
                WHERE MATCH(ef-(r)->et)
                  AND r.ScopeKey = ef.ScopeKey AND et.ScopeKey = ef.ScopeKey
            )
            SELECT COUNT(*) FROM RelData {where}
            """;

        await using var cmd = new SqlCommand(sql, conn);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DeleteRelationshipAsync(string fromEntityName, string fromEntityType, string toEntityName, string toEntityType, string relationshipType, string scopeKey, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            DELETE r
            FROM {Schema}.KnowledgeRelationship r,
                 {Schema}.KnowledgeEntity ef,
                 {Schema}.KnowledgeEntity et
            WHERE MATCH(ef-(r)->et)
              AND ef.Name = @fromName AND ef.EntityType = @fromType
              AND et.Name = @toName AND et.EntityType = @toType
              AND ef.ScopeKey = @scopeKey AND et.ScopeKey = @scopeKey AND r.ScopeKey = @scopeKey
              AND r.RelationshipType = @relType
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fromName", fromEntityName);
        cmd.Parameters.AddWithValue("@fromType", fromEntityType);
        cmd.Parameters.AddWithValue("@toName", toEntityName);
        cmd.Parameters.AddWithValue("@toType", toEntityType);
        cmd.Parameters.AddWithValue("@relType", relationshipType);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Admin: Deleted relationship {From}-[{Type}]->{To}", fromEntityName, relationshipType, toEntityName);
    }

    public async Task<IReadOnlyList<string>> ListRelationshipTypesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"SELECT DISTINCT RelationshipType FROM {Schema}.KnowledgeRelationship ORDER BY RelationshipType";
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<string>();
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    // ─── Domains ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminDomainDto>> ListDomainsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            SELECT d.DomainId, d.Name, d.Description, d.PriorityWeight, d.Metadata, d.CreatedAt,
                   ISNULL(catCnt.Cnt, 0) AS CategoryCount,
                   ISNULL(entCnt.Cnt, 0) AS EntityCount
            FROM {Schema}.KnowledgeDomain d
            LEFT JOIN (
                SELECT d2.$node_id AS DomainNodeId, COUNT(*) AS Cnt
                FROM {Schema}.BelongsTo bt, {Schema}.KnowledgeCategory cat, {Schema}.KnowledgeDomain d2
                WHERE MATCH(cat-(bt)->d2)
                GROUP BY d2.$node_id
            ) catCnt ON d.$node_id = catCnt.DomainNodeId
            LEFT JOIN (
                SELECT d3.$node_id AS DomainNodeId, COUNT(*) AS Cnt
                FROM {Schema}.BelongsTo bt1, {Schema}.KnowledgeEntity e, {Schema}.KnowledgeCategory cat,
                     {Schema}.BelongsTo bt2, {Schema}.KnowledgeDomain d3
                WHERE MATCH(e-(bt1)->cat-(bt2)->d3)
                GROUP BY d3.$node_id
            ) entCnt ON d.$node_id = entCnt.DomainNodeId
            ORDER BY d.Name
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<AdminDomainDto>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AdminDomainDto
            {
                DomainId = reader.GetGuid(reader.GetOrdinal("DomainId")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                PriorityWeight = reader.GetDouble(reader.GetOrdinal("PriorityWeight")),
                Metadata = reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                CategoryCount = reader.GetInt32(reader.GetOrdinal("CategoryCount")),
                EntityCount = reader.GetInt32(reader.GetOrdinal("EntityCount"))
            });
        }
        return list;
    }

    public async Task<AdminDomainDto> CreateDomainAsync(string name, string? description, double priorityWeight = 1.0, string? metadata = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            INSERT INTO {Schema}.KnowledgeDomain (Name, Description, PriorityWeight, Metadata)
            OUTPUT INSERTED.DomainId, INSERTED.Name, INSERTED.Description, INSERTED.PriorityWeight,
                   INSERTED.Metadata, INSERTED.CreatedAt
            VALUES (@name, @description, @priorityWeight, @metadata)
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@priorityWeight", priorityWeight);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        _logger.LogInformation("Admin: Created domain '{Name}'", name);
        return new AdminDomainDto
        {
            DomainId = reader.GetGuid(0),
            Name = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            PriorityWeight = reader.GetDouble(3),
            Metadata = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5)
        };
    }

    public async Task<AdminDomainDto> UpdateDomainAsync(Guid domainId, string? description, double priorityWeight = 1.0, string? metadata = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            UPDATE {Schema}.KnowledgeDomain
            SET Description = @description, PriorityWeight = @priorityWeight, Metadata = @metadata
            WHERE DomainId = @domainId;

            SELECT DomainId, Name, Description, PriorityWeight, Metadata, CreatedAt
            FROM {Schema}.KnowledgeDomain WHERE DomainId = @domainId;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@domainId", domainId);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@priorityWeight", priorityWeight);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        _logger.LogInformation("Admin: Updated domain {DomainId}", domainId);
        return new AdminDomainDto
        {
            DomainId = reader.GetGuid(0),
            Name = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            PriorityWeight = reader.GetDouble(3),
            Metadata = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5)
        };
    }

    public async Task DeleteDomainAsync(Guid domainId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            -- Delete BelongsTo edges pointing to this domain (Category→Domain)
            DELETE bt FROM {Schema}.BelongsTo bt
            INNER JOIN {Schema}.KnowledgeDomain d ON d.$node_id = bt.$to_id
            WHERE d.DomainId = @domainId;

            DELETE FROM {Schema}.KnowledgeDomain WHERE DomainId = @domainId;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@domainId", domainId);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Admin: Deleted domain {DomainId}", domainId);
    }

    // ─── Categories ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminCategoryDto>> ListCategoriesAsync(string? domainNameFilter = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var whereClause = "";
        if (!string.IsNullOrWhiteSpace(domainNameFilter))
            whereClause = "WHERE d.Name = @domainName";

        var sql = $"""
            ;WITH CatData AS (
                SELECT cat.CategoryId, cat.Name, cat.Description,
                       CASE WHEN cat.Embedding IS NOT NULL THEN 1 ELSE 0 END AS HasEmbedding,
                       cat.Metadata, cat.CreatedAt, d.Name AS DomainName
                FROM {Schema}.KnowledgeCategory cat
                LEFT JOIN {Schema}.BelongsTo bt ON cat.$node_id = bt.$from_id
                LEFT JOIN {Schema}.KnowledgeDomain d ON bt.$to_id = d.$node_id
            )
            SELECT cd.*, ISNULL(entCnt.Cnt, 0) AS EntityCount
            FROM CatData cd
            LEFT JOIN (
                SELECT cat2.CategoryId, COUNT(*) AS Cnt
                FROM {Schema}.BelongsTo bt2, {Schema}.KnowledgeEntity e, {Schema}.KnowledgeCategory cat2
                WHERE MATCH(e-(bt2)->cat2)
                GROUP BY cat2.CategoryId
            ) entCnt ON cd.CategoryId = entCnt.CategoryId
            {whereClause}
            ORDER BY cd.Name
            """;

        await using var cmd = new SqlCommand(sql, conn);
        if (!string.IsNullOrWhiteSpace(domainNameFilter))
            cmd.Parameters.AddWithValue("@domainName", domainNameFilter);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AdminCategoryDto>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AdminCategoryDto
            {
                CategoryId = reader.GetGuid(reader.GetOrdinal("CategoryId")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                HasEmbedding = reader.GetInt32(reader.GetOrdinal("HasEmbedding")) == 1,
                Metadata = reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                DomainName = reader.IsDBNull(reader.GetOrdinal("DomainName")) ? null : reader.GetString(reader.GetOrdinal("DomainName")),
                EntityCount = reader.GetInt32(reader.GetOrdinal("EntityCount"))
            });
        }
        return list;
    }

    public async Task<AdminCategoryDto> CreateCategoryAsync(string name, string domainName, string? description, string? metadata = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Insert category and create BelongsTo edge to domain
        var sql = $"""
            INSERT INTO {Schema}.KnowledgeCategory (Name, Description, Metadata)
            VALUES (@name, @description, @metadata);

            INSERT INTO {Schema}.BelongsTo ($from_id, $to_id, Metadata)
            SELECT cat.$node_id, d.$node_id, NULL
            FROM {Schema}.KnowledgeCategory cat, {Schema}.KnowledgeDomain d
            WHERE cat.Name = @name AND d.Name = @domainName;

            SELECT cat.CategoryId, cat.Name, cat.Description,
                   CASE WHEN cat.Embedding IS NOT NULL THEN 1 ELSE 0 END AS HasEmbedding,
                   cat.Metadata, cat.CreatedAt
            FROM {Schema}.KnowledgeCategory cat WHERE cat.Name = @name;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@domainName", domainName);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        _logger.LogInformation("Admin: Created category '{Name}' under domain '{Domain}'", name, domainName);
        return new AdminCategoryDto
        {
            CategoryId = reader.GetGuid(0),
            Name = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            HasEmbedding = reader.GetInt32(3) == 1,
            Metadata = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            DomainName = domainName
        };
    }

    public async Task<AdminCategoryDto> UpdateCategoryAsync(Guid categoryId, string? description, string? metadata = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            UPDATE {Schema}.KnowledgeCategory
            SET Description = @description, Metadata = @metadata
            WHERE CategoryId = @categoryId;

            SELECT cat.CategoryId, cat.Name, cat.Description,
                   CASE WHEN cat.Embedding IS NOT NULL THEN 1 ELSE 0 END AS HasEmbedding,
                   cat.Metadata, cat.CreatedAt, d.Name AS DomainName
            FROM {Schema}.KnowledgeCategory cat
            LEFT JOIN {Schema}.BelongsTo bt ON cat.$node_id = bt.$from_id
            LEFT JOIN {Schema}.KnowledgeDomain d ON bt.$to_id = d.$node_id
            WHERE cat.CategoryId = @categoryId;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@categoryId", categoryId);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        _logger.LogInformation("Admin: Updated category {CategoryId}", categoryId);
        return new AdminCategoryDto
        {
            CategoryId = reader.GetGuid(0),
            Name = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            HasEmbedding = reader.GetInt32(3) == 1,
            Metadata = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            DomainName = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"""
            -- Delete BelongsTo edges where this category is source (Category→Domain)
            DELETE bt FROM {Schema}.BelongsTo bt
            INNER JOIN {Schema}.KnowledgeCategory cat ON cat.$node_id = bt.$from_id
            WHERE cat.CategoryId = @categoryId;

            -- Delete BelongsTo edges where this category is target (Entity→Category)
            DELETE bt FROM {Schema}.BelongsTo bt
            INNER JOIN {Schema}.KnowledgeCategory cat ON cat.$node_id = bt.$to_id
            WHERE cat.CategoryId = @categoryId;

            -- Delete community summaries
            DELETE FROM {Schema}.CommunitySummary WHERE CategoryId = @categoryId;

            DELETE FROM {Schema}.KnowledgeCategory WHERE CategoryId = @categoryId;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@categoryId", categoryId);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Admin: Deleted category {CategoryId}", categoryId);
    }

    // ─── Graph Visualization ─────────────────────────────────────────────

    public async Task<GraphData> GetGraphDataAsync(string? scopeFilter = null, int maxNodes = 200, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Get top N entities (most connected first)
        var scopeWhere = string.IsNullOrWhiteSpace(scopeFilter) ? "" : "WHERE e.ScopeKey = @scope";
        var nodesSql = $"""
            SELECT TOP(@maxNodes) e.EntityId, e.Name, e.EntityType, e.ScopeKey, e.Description,
                   ISNULL(cc.Cnt, 0) AS ChunkCount,
                   d.Name AS DomainName, cat.Name AS CategoryName
            FROM {Schema}.KnowledgeEntity e
            LEFT JOIN (SELECT EntityId, COUNT(*) AS Cnt FROM {Schema}.KnowledgeChunk GROUP BY EntityId) cc ON cc.EntityId = e.EntityId
            LEFT JOIN {Schema}.BelongsTo bt_ec ON e.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = e.ScopeKey
            LEFT JOIN {Schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
            LEFT JOIN {Schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
            LEFT JOIN {Schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
            {scopeWhere}
            ORDER BY e.Name
            """;

        await using var nodesCmd = new SqlCommand(nodesSql, conn);
        nodesCmd.Parameters.AddWithValue("@maxNodes", maxNodes);
        if (!string.IsNullOrWhiteSpace(scopeFilter))
            nodesCmd.Parameters.AddWithValue("@scope", scopeFilter);

        var nodes = new List<GraphNode>();
        var nodeIds = new HashSet<string>();

        await using (var reader = await nodesCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(reader.GetOrdinal("EntityId")).ToString();
                nodeIds.Add(reader.GetString(reader.GetOrdinal("Name")));
                nodes.Add(new GraphNode
                {
                    Id = id,
                    Name = reader.GetString(reader.GetOrdinal("Name")),
                    EntityType = reader.GetString(reader.GetOrdinal("EntityType")),
                    ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
                    Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                    ChunkCount = reader.GetInt32(reader.GetOrdinal("ChunkCount")),
                    DomainName = reader.IsDBNull(reader.GetOrdinal("DomainName")) ? null : reader.GetString(reader.GetOrdinal("DomainName")),
                    CategoryName = reader.IsDBNull(reader.GetOrdinal("CategoryName")) ? null : reader.GetString(reader.GetOrdinal("CategoryName"))
                });
            }
        }

        // Get relationships between those nodes
        var nodeNameLookup = nodes.ToDictionary(n => n.Name, n => n.Id);

        var linksSql = $"""
            ;WITH RelData AS (
                SELECT r.RelationshipType, r.Weight, r.Description,
                       ef.Name AS FromName, ef.EntityId AS FromId,
                       et.Name AS ToName, et.EntityId AS ToId
                FROM {Schema}.KnowledgeRelationship r,
                     {Schema}.KnowledgeEntity ef,
                     {Schema}.KnowledgeEntity et
                WHERE MATCH(ef-(r)->et)
            )
            SELECT * FROM RelData
            """;

        await using var linksCmd = new SqlCommand(linksSql, conn);
        var links = new List<GraphLink>();

        await using (var reader = await linksCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var fromName = reader.GetString(reader.GetOrdinal("FromName"));
                var toName = reader.GetString(reader.GetOrdinal("ToName"));

                if (nodeNameLookup.TryGetValue(fromName, out var sourceId) &&
                    nodeNameLookup.TryGetValue(toName, out var targetId))
                {
                    links.Add(new GraphLink
                    {
                        Source = sourceId,
                        Target = targetId,
                        RelationshipType = reader.GetString(reader.GetOrdinal("RelationshipType")),
                        Weight = reader.GetDouble(reader.GetOrdinal("Weight")),
                        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description"))
                    });
                }
            }
        }

        return new GraphData { Nodes = nodes, Links = links };
    }

    // ─── Search ──────────────────────────────────────────────────────────

    public async Task<AdminSearchResult> SearchAsync(string query, IReadOnlyList<string> scopes, string searchType, int limit = 10, string? entityTypeFilter = null, string? domainFilter = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Try IKnowledgeSearchService first (available when host has IEmbeddings).
            // Fall back to direct SQL + Host API embeddings for client-only hosts.
            // The service may be registered but its factory can throw if IEmbeddings
            // is missing, so we catch resolution failures and fall back.
            IKnowledgeSearchService? searchService = null;
            try
            {
                searchService = _serviceProvider.GetService(typeof(IKnowledgeSearchService)) as IKnowledgeSearchService;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("IEmbeddings"))
            {
                _logger.LogDebug("IKnowledgeSearchService unavailable (no IEmbeddings), using Host API fallback");
            }

            string json;
            if (searchService is not null)
            {
                json = await SearchViaServiceAsync(searchService, query, scopes, searchType, limit, entityTypeFilter, domainFilter, ct);
            }
            else
            {
                json = await SearchDirectAsync(query, scopes, searchType, limit, entityTypeFilter, domainFilter, ct);
            }

            sw.Stop();

            int count = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    count = doc.RootElement.GetArrayLength();
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    count = 1;
            }
            catch { count = 0; }

            return new AdminSearchResult
            {
                SearchType = searchType,
                RawJson = json,
                ResultCount = count,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Admin search failed: {SearchType} '{Query}'", searchType, query);
            return new AdminSearchResult
            {
                SearchType = searchType,
                RawJson = "[]",
                ErrorMessage = ex.Message,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    private static async Task<string> SearchViaServiceAsync(
        IKnowledgeSearchService searchService, string query, IReadOnlyList<string> scopes,
        string searchType, int limit, string? entityTypeFilter, string? domainFilter, CancellationToken ct)
    {
        var request = new ScopedSearchRequest(query, scopes, limit, entityTypeFilter, domainFilter);
        return searchType.ToLowerInvariant() switch
        {
            "entities" => await searchService.SearchEntitiesAsync(request, ct),
            "chunks" => await searchService.SearchChunksAsync(request, ct),
            "relationships" => await searchService.SearchRelationshipsAsync(
                new ScopedRelationshipRequest(query, "Entity", scopes), ct),
            "hybrid" => await searchService.HybridSearchAsync(request, graphDepth: 2, vectorLimit: limit, ct: ct),
            _ => await searchService.SearchEntitiesAsync(request, ct)
        };
    }

    /// <summary>
    /// Fallback search for client-only hosts (no IEmbeddings). Calls the
    /// FabrCore Host API <c>/fabrcoreapi/embeddings</c> for vector generation,
    /// then runs the search SQL directly.
    /// </summary>
    private async Task<string> SearchDirectAsync(
        string query, IReadOnlyList<string> scopes, string searchType, int limit,
        string? entityTypeFilter, string? domainFilter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_hostApiBaseUrl) || _httpClientFactory is null)
            throw new InvalidOperationException(
                "Search requires either IKnowledgeSearchService (via AddFabrCoreServer) " +
                "or FabrCoreHostUrl + IHttpClientFactory for remote embeddings.");

        var embedding = await GetEmbeddingFromHostApiAsync(query, ct);
        var vectorParam = "[" + string.Join(",", embedding.Select(v => v.ToString("G"))) + "]";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var scopeFilter = BuildScopeInClause("e", scopes);

        string sql;
        switch (searchType.ToLowerInvariant())
        {
            case "chunks":
                sql = BuildChunkSearchSql(scopeFilter, entityTypeFilter, domainFilter);
                break;
            case "relationships":
                return await SearchRelationshipsDirectAsync(conn, query, scopes, ct);
            default: // entities, hybrid
                sql = BuildEntitySearchSql(scopeFilter, entityTypeFilter, domainFilter);
                break;
        }

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@queryVector", vectorParam);
        AddScopeParameters(cmd, scopes);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[ToCamelCase(name)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }

        return JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    private async Task<float[]> GetEmbeddingFromHostApiAsync(string text, CancellationToken ct)
    {
        var client = _httpClientFactory!.CreateClient();
        client.BaseAddress = new Uri(_hostApiBaseUrl!.TrimEnd('/'));

        var response = await client.PostAsJsonAsync("/fabrcoreapi/Embeddings", new { Text = text }, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var vectorElement = doc.RootElement.GetProperty("vector");
        var vector = new float[vectorElement.GetArrayLength()];
        int idx = 0;
        foreach (var item in vectorElement.EnumerateArray())
            vector[idx++] = item.GetSingle();
        return vector;
    }

    private string BuildEntitySearchSql(string scopeFilter, string? entityTypeFilter, string? domainFilter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
            SELECT TOP(@limit)
                e.EntityId, e.Name, e.EntityType, e.ScopeKey, e.Description, e.Metadata,
                VECTOR_DISTANCE('cosine', e.Embedding, CAST(@queryVector AS VECTOR(1536))) AS Distance,
                d.Name AS DomainName, cat.Name AS CategoryName
            FROM {Schema}.KnowledgeEntity e
            LEFT JOIN {Schema}.BelongsTo bt_ec ON e.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = e.ScopeKey
            LEFT JOIN {Schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
            LEFT JOIN {Schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
            LEFT JOIN {Schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
            WHERE e.Embedding IS NOT NULL AND {scopeFilter}
            """);

        if (!string.IsNullOrWhiteSpace(entityTypeFilter))
            sb.AppendLine($"  AND e.EntityType = '{entityTypeFilter.Replace("'", "''")}'");
        if (!string.IsNullOrWhiteSpace(domainFilter))
            sb.AppendLine($"  AND d.Name = '{domainFilter.Replace("'", "''")}'");

        sb.AppendLine("ORDER BY Distance");
        return sb.ToString();
    }

    private string BuildChunkSearchSql(string scopeFilter, string? entityTypeFilter, string? domainFilter)
    {
        var chunkScopeFilter = scopeFilter.Replace("e.ScopeKey", "c.ScopeKey");
        var sb = new StringBuilder();
        sb.AppendLine($"""
            SELECT TOP(@limit)
                c.ChunkId, c.EntityId, c.Content, c.ChunkIndex, c.ScopeKey,
                VECTOR_DISTANCE('cosine', c.Embedding, CAST(@queryVector AS VECTOR(1536))) AS Distance,
                e.Name AS EntityName, e.EntityType
            FROM {Schema}.KnowledgeChunk c
            INNER JOIN {Schema}.KnowledgeEntity e ON c.EntityId = e.EntityId
            WHERE c.Embedding IS NOT NULL AND {chunkScopeFilter}
            """);

        if (!string.IsNullOrWhiteSpace(entityTypeFilter))
            sb.AppendLine($"  AND e.EntityType = '{entityTypeFilter.Replace("'", "''")}'");

        sb.AppendLine("ORDER BY Distance");
        return sb.ToString();
    }

    private async Task<string> SearchRelationshipsDirectAsync(
        SqlConnection conn, string entityName, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var scopeFilter = BuildScopeInClause("ef", scopes);
        var sql = $"""
            ;WITH RelData AS (
                SELECT r.RelationshipType, r.Description, r.Weight,
                       ef.Name AS FromEntityName, ef.EntityType AS FromEntityType,
                       et.Name AS ToEntityName, et.EntityType AS ToEntityType, ef.ScopeKey
                FROM {Schema}.KnowledgeRelationship r,
                     {Schema}.KnowledgeEntity ef,
                     {Schema}.KnowledgeEntity et
                WHERE MATCH(ef-(r)->et)
            )
            SELECT TOP(50) * FROM RelData
            WHERE {scopeFilter.Replace("ef.ScopeKey", "ScopeKey")}
              AND (FromEntityName LIKE @search OR ToEntityName LIKE @search)
            ORDER BY FromEntityName
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@search", $"%{entityName}%");
        AddScopeParameters(cmd, scopes);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[ToCamelCase(reader.GetName(i))] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            results.Add(row);
        }

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string BuildScopeInClause(string alias, IReadOnlyList<string> scopes)
    {
        var paramNames = Enumerable.Range(0, scopes.Count).Select(i => $"@__scope{i}");
        return $"{alias}.ScopeKey IN ({string.Join(", ", paramNames)})";
    }

    private static void AddScopeParameters(SqlCommand cmd, IReadOnlyList<string> scopes)
    {
        for (int i = 0; i < scopes.Count; i++)
            cmd.Parameters.AddWithValue($"@__scope{i}", scopes[i]);
    }

    private static string ToCamelCase(string name)
        => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static (string where, List<SqlParameter> parameters) BuildEntityWhereClause(
        string? scopeFilter, string? entityTypeFilter, string? searchTerm)
    {
        var conditions = new List<string>();
        var parameters = new List<SqlParameter>();

        if (!string.IsNullOrWhiteSpace(scopeFilter))
        {
            conditions.Add("e.ScopeKey = @scopeFilter");
            parameters.Add(new SqlParameter("@scopeFilter", scopeFilter));
        }
        if (!string.IsNullOrWhiteSpace(entityTypeFilter))
        {
            conditions.Add("e.EntityType = @entityTypeFilter");
            parameters.Add(new SqlParameter("@entityTypeFilter", entityTypeFilter));
        }
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            conditions.Add("(e.Name LIKE @searchTerm OR e.Description LIKE @searchTerm)");
            parameters.Add(new SqlParameter("@searchTerm", $"%{searchTerm}%"));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (where, parameters);
    }

    private static (string where, List<SqlParameter> parameters) BuildRelationshipWhereClause(
        string? scopeFilter, string? entityNameFilter, string? relationshipTypeFilter)
    {
        var conditions = new List<string>();
        var parameters = new List<SqlParameter>();

        if (!string.IsNullOrWhiteSpace(scopeFilter))
        {
            conditions.Add("ScopeKey = @scopeFilter");
            parameters.Add(new SqlParameter("@scopeFilter", scopeFilter));
        }
        if (!string.IsNullOrWhiteSpace(entityNameFilter))
        {
            conditions.Add("(FromEntityName LIKE @entityNameFilter OR ToEntityName LIKE @entityNameFilter)");
            parameters.Add(new SqlParameter("@entityNameFilter", $"%{entityNameFilter}%"));
        }
        if (!string.IsNullOrWhiteSpace(relationshipTypeFilter))
        {
            conditions.Add("RelationshipType = @relTypeFilter");
            parameters.Add(new SqlParameter("@relTypeFilter", relationshipTypeFilter));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return (where, parameters);
    }

    private static AdminScopeDto ReadScopeDto(SqlDataReader reader) => new()
    {
        ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
        DefaultPriority = reader.GetDouble(reader.GetOrdinal("DefaultPriority")),
        Metadata = reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata")),
        CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
        EntityCount = reader.GetInt32(reader.GetOrdinal("EntityCount"))
    };

    private static AdminEntityDto ReadEntityDto(SqlDataReader reader) => new()
    {
        EntityId = reader.GetGuid(reader.GetOrdinal("EntityId")),
        CanonicalEntityId = reader.GetGuid(reader.GetOrdinal("CanonicalEntityId")),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        EntityType = reader.GetString(reader.GetOrdinal("EntityType")),
        ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
        HasEmbedding = reader.GetInt32(reader.GetOrdinal("HasEmbedding")) == 1,
        Metadata = reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata")),
        CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
        UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
        ChunkCount = reader.GetInt32(reader.GetOrdinal("ChunkCount")),
        DomainName = reader.IsDBNull(reader.GetOrdinal("DomainName")) ? null : reader.GetString(reader.GetOrdinal("DomainName")),
        CategoryName = reader.IsDBNull(reader.GetOrdinal("CategoryName")) ? null : reader.GetString(reader.GetOrdinal("CategoryName"))
    };

    private static AdminRelationshipDto ReadRelationshipDto(SqlDataReader reader) => new()
    {
        RelationshipType = reader.GetString(reader.GetOrdinal("RelationshipType")),
        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
        Weight = reader.GetDouble(reader.GetOrdinal("Weight")),
        Metadata = reader.IsDBNull(reader.GetOrdinal("Metadata")) ? null : reader.GetString(reader.GetOrdinal("Metadata")),
        CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
        FromEntityName = reader.GetString(reader.GetOrdinal("FromEntityName")),
        FromEntityType = reader.GetString(reader.GetOrdinal("FromEntityType")),
        ToEntityName = reader.GetString(reader.GetOrdinal("ToEntityName")),
        ToEntityType = reader.GetString(reader.GetOrdinal("ToEntityType")),
        ScopeKey = reader.IsDBNull(reader.GetOrdinal("ScopeKey")) ? null : reader.GetString(reader.GetOrdinal("ScopeKey"))
    };

    // ─── Orphan Taxonomy ─────────────────────────────────────────────────

    public async Task<OrphanTaxonomyReport> GetOrphanTaxonomyAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var domainSql = $"""
            SELECT d.DomainId, d.Name, d.Description, d.PriorityWeight, d.Metadata, d.CreatedAt
            FROM {Schema}.KnowledgeDomain d
            WHERE NOT EXISTS (
                SELECT 1 FROM {Schema}.DocumentContribution dc
                WHERE dc.DomainId = d.DomainId
            )
            ORDER BY d.Name
            """;

        var domains = new List<AdminDomainDto>();
        await using (var cmd = new SqlCommand(domainSql, conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                domains.Add(new AdminDomainDto
                {
                    DomainId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    PriorityWeight = reader.GetDouble(3),
                    Metadata = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = reader.GetDateTime(5)
                });
            }
        }

        var catSql = $"""
            SELECT c.CategoryId, c.Name, c.Description, c.Metadata, c.CreatedAt
            FROM {Schema}.KnowledgeCategory c
            WHERE NOT EXISTS (
                SELECT 1 FROM {Schema}.DocumentContribution dc
                WHERE dc.CategoryId = c.CategoryId
            )
            ORDER BY c.Name
            """;

        var categories = new List<AdminCategoryDto>();
        await using (var cmd = new SqlCommand(catSql, conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                categories.Add(new AdminCategoryDto
                {
                    CategoryId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Metadata = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAt = reader.GetDateTime(4)
                });
            }
        }

        return new OrphanTaxonomyReport { Domains = domains, Categories = categories };
    }

    public async Task PurgeOrphanTaxonomyAsync(
        IEnumerable<Guid> domainIds, IEnumerable<Guid> categoryIds, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        foreach (var categoryId in categoryIds)
        {
            // Safety check — refuse if a DocumentContribution still references this category.
            await using (var check = new SqlCommand(
                $"SELECT TOP(1) 1 FROM {Schema}.DocumentContribution WHERE CategoryId = @id", conn, tx))
            {
                check.Parameters.AddWithValue("@id", categoryId);
                if (await check.ExecuteScalarAsync(ct) is not null)
                    continue; // Still referenced — skip.
            }

            await using (var cmd = new SqlCommand(
                $"""
                DELETE bt FROM {Schema}.BelongsTo bt
                INNER JOIN {Schema}.KnowledgeCategory c ON bt.$from_id = c.$node_id OR bt.$to_id = c.$node_id
                WHERE c.CategoryId = @id
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", categoryId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new SqlCommand(
                $"DELETE FROM {Schema}.CommunitySummary WHERE CategoryId = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", categoryId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new SqlCommand(
                $"DELETE FROM {Schema}.KnowledgeCategory WHERE CategoryId = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", categoryId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        foreach (var domainId in domainIds)
        {
            await using (var check = new SqlCommand(
                $"SELECT TOP(1) 1 FROM {Schema}.DocumentContribution WHERE DomainId = @id", conn, tx))
            {
                check.Parameters.AddWithValue("@id", domainId);
                if (await check.ExecuteScalarAsync(ct) is not null)
                    continue;
            }

            await using (var cmd = new SqlCommand(
                $"""
                DELETE bt FROM {Schema}.BelongsTo bt
                INNER JOIN {Schema}.KnowledgeDomain d ON bt.$from_id = d.$node_id OR bt.$to_id = d.$node_id
                WHERE d.DomainId = @id
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", domainId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new SqlCommand(
                $"DELETE FROM {Schema}.KnowledgeDomain WHERE DomainId = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", domainId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Purged orphan taxonomy: {Domains} domains, {Categories} categories requested",
            domainIds.Count(), categoryIds.Count());
    }

    // ─── Ingestion Metrics ───────────────────────────────────────────────

    public async Task<IngestionMetricsSummaryDto> GetMetricsSummaryAsync(
        string? scope, DateTime? since, int topN = 25, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope)) whereClauses.Add("ScopeKey = @scope");
        if (since is not null) whereClauses.Add("CreatedAt >= @since");
        var where = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        // KPI strip + ingestion-run count.
        var summarySql = $"""
            SELECT
                ISNULL(SUM(ChatInputTokens), 0)  AS TotalChatInputTokens,
                ISNULL(SUM(ChatOutputTokens), 0) AS TotalChatOutputTokens,
                ISNULL(SUM(ChatCallCount), 0)    AS TotalChatCalls,
                COUNT(*)                         AS TotalIngestionRuns,
                ISNULL(SUM(DurationMs), 0)        AS TotalDurationMs,
                ISNULL(SUM(ChatTotalMs), 0)       AS TotalChatMs,
                ISNULL(SUM(LlmExtractionMs), 0)  AS TotalLlmExtractionMs,
                ISNULL(SUM(ChunkEmbeddingMs + DocumentEmbeddingMs + EntityEmbeddingMs), 0) AS TotalEmbeddingMs,
                ISNULL(SUM(SqlWriteMs), 0)        AS TotalSqlWriteMs,
                ISNULL(SUM(EmbeddingBatchCount), 0) AS TotalEmbeddingBatches,
                ISNULL(SUM(ExtractionBatchCount), 0) AS TotalExtractionBatches,
                ISNULL(SUM(ExtractionRetryCount), 0) AS TotalExtractionRetries,
                ISNULL(SUM(ExtractionTruncationCount), 0) AS TotalExtractionTruncations
            FROM {Schema}.IngestionMetric
            {where};
            """;

        var summary = new IngestionMetricsSummaryDto();
        await using (var cmd = new SqlCommand(summarySql, conn))
        {
            if (!string.IsNullOrWhiteSpace(scope)) cmd.Parameters.AddWithValue("@scope", scope);
            if (since is not null) cmd.Parameters.AddWithValue("@since", since.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                summary.TotalChatInputTokens = reader.GetInt64(0);
                summary.TotalChatOutputTokens = reader.GetInt64(1);
                summary.TotalChatCalls = (int)Math.Min(int.MaxValue, Convert.ToInt64(reader.GetValue(2)));
                summary.TotalIngestionRuns = reader.GetInt32(3);
                summary.TotalDurationMs = reader.GetInt64(4);
                summary.TotalChatMs = reader.GetInt64(5);
                summary.TotalLlmExtractionMs = reader.GetInt64(6);
                summary.TotalEmbeddingMs = reader.GetInt64(7);
                summary.TotalSqlWriteMs = reader.GetInt64(8);
                summary.TotalEmbeddingBatches = (int)Math.Min(int.MaxValue, Convert.ToInt64(reader.GetValue(9)));
                summary.TotalExtractionBatches = (int)Math.Min(int.MaxValue, Convert.ToInt64(reader.GetValue(10)));
                summary.TotalExtractionRetries = (int)Math.Min(int.MaxValue, Convert.ToInt64(reader.GetValue(11)));
                summary.TotalExtractionTruncations = (int)Math.Min(int.MaxValue, Convert.ToInt64(reader.GetValue(12)));
            }
        }

        // Top-N most expensive documents (sum of all runs).
        var topSql = $"""
            SELECT TOP (@topN)
                im.DocumentId,
                sd.FileName,
                sd.ScopeKey,
                COUNT(*)                         AS Runs,
                SUM(im.ChatInputTokens)          AS ChatInputTokens,
                SUM(im.ChatOutputTokens)         AS ChatOutputTokens,
                SUM(im.DurationMs)               AS TotalDurationMs,
                AVG(CAST(im.DurationMs AS FLOAT)) AS AverageDurationMs,
                MAX(im.CreatedAt)                AS LastIngestedAt
            FROM {Schema}.IngestionMetric im
            JOIN {Schema}.SourceDocument sd ON sd.DocumentId = im.DocumentId
            {(where.Length > 0 ? where.Replace("ScopeKey", "im.ScopeKey").Replace("CreatedAt", "im.CreatedAt") : "")}
            GROUP BY im.DocumentId, sd.FileName, sd.ScopeKey
            ORDER BY SUM(im.ChatInputTokens + im.ChatOutputTokens) DESC;
            """;

        var top = new List<TopDocumentDto>();
        await using (var cmd = new SqlCommand(topSql, conn))
        {
            cmd.Parameters.AddWithValue("@topN", topN);
            if (!string.IsNullOrWhiteSpace(scope)) cmd.Parameters.AddWithValue("@scope", scope);
            if (since is not null) cmd.Parameters.AddWithValue("@since", since.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                top.Add(new TopDocumentDto
                {
                    DocumentId = reader.GetGuid(0),
                    FileName = reader.GetString(1),
                    ScopeKey = reader.GetString(2),
                    Runs = reader.GetInt32(3),
                    ChatInputTokens = reader.GetInt64(4),
                    ChatOutputTokens = reader.GetInt64(5),
                    TotalDurationMs = reader.GetInt64(6),
                    AverageDurationMs = reader.GetDouble(7),
                    LastIngestedAt = reader.GetDateTime(8)
                });
            }
        }
        summary.TopDocuments = top;

        // Re-ingest amplification — documents with more than one run.
        var ampSql = $"""
            SELECT
                im.DocumentId,
                sd.FileName,
                COUNT(*) AS Runs,
                MIN(im.ChatInputTokens + im.ChatOutputTokens) AS FirstRunTokens,
                SUM(im.ChatInputTokens + im.ChatOutputTokens) AS TotalTokens
            FROM {Schema}.IngestionMetric im
            JOIN {Schema}.SourceDocument sd ON sd.DocumentId = im.DocumentId
            {(where.Length > 0 ? where.Replace("ScopeKey", "im.ScopeKey").Replace("CreatedAt", "im.CreatedAt") : "")}
            GROUP BY im.DocumentId, sd.FileName
            HAVING COUNT(*) > 1
            ORDER BY SUM(im.ChatInputTokens + im.ChatOutputTokens) DESC;
            """;

        var amps = new List<ReingestAmplificationDto>();
        await using (var cmd = new SqlCommand(ampSql, conn))
        {
            if (!string.IsNullOrWhiteSpace(scope)) cmd.Parameters.AddWithValue("@scope", scope);
            if (since is not null) cmd.Parameters.AddWithValue("@since", since.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                amps.Add(new ReingestAmplificationDto
                {
                    DocumentId = reader.GetGuid(0),
                    FileName = reader.GetString(1),
                    Runs = reader.GetInt32(2),
                    FirstRunTokens = reader.GetInt64(3),
                    TotalTokens = reader.GetInt64(4)
                });
            }
        }
        summary.ReingestAmplifications = amps;

        return summary;
    }

    public async Task<IReadOnlyList<DocumentTokenSummaryDto>> GetDocumentTokenSummariesAsync(
        IReadOnlyList<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return Array.Empty<DocumentTokenSummaryDto>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Build a parameterized IN clause. SQL Server caps at 2100 parameters
        // per command — well above the page sizes the IngestionTab uses.
        var paramNames = new string[documentIds.Count];
        for (var i = 0; i < documentIds.Count; i++) paramNames[i] = $"@d{i}";

        var sql = $"""
            SELECT
                im.DocumentId,
                SUM(im.ChatInputTokens)  AS ChatInputTokens,
                SUM(im.ChatOutputTokens) AS ChatOutputTokens
            FROM {Schema}.IngestionMetric im
            WHERE im.DocumentId IN ({string.Join(",", paramNames)})
            GROUP BY im.DocumentId;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        for (var i = 0; i < documentIds.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], documentIds[i]);

        var results = new List<DocumentTokenSummaryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DocumentTokenSummaryDto
            {
                DocumentId = reader.GetGuid(0),
                ChatInputTokens = reader.GetInt64(1),
                ChatOutputTokens = reader.GetInt64(2)
            });
        }
        return results;
    }
}
