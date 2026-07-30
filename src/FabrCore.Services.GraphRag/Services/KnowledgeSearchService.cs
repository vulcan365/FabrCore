using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// SQL-backed implementation of <see cref="IKnowledgeSearchService"/>. All
/// vector search SQL for the GraphRAG schema lives in this class. Scope is
/// enforced via an <c>e.ScopeKey IN (...)</c> filter on every statement.
/// Multi-scope searches treat every listed scope on equal footing — results
/// are ranked strictly by raw vector distance and scope list ordering does
/// not influence the outcome.
/// </summary>
public sealed class KnowledgeSearchService : IKnowledgeSearchService
{
    private readonly string _connectionString;
    private readonly IEmbeddings? _embeddings;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string? _hostApiBaseUrl;
    private readonly ILogger<KnowledgeSearchService> _logger;
    private readonly IGraphRagAuditLog _audit;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public KnowledgeSearchService(
        IConfiguration configuration,
        ILogger<KnowledgeSearchService> logger,
        string connectionStringName,
        IGraphRagAuditLog audit,
        IEmbeddings? embeddings = null,
        IHttpClientFactory? httpClientFactory = null,
        string? hostApiBaseUrl = null)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrWhiteSpace(connectionStringName))
            throw new ArgumentException("Connection string name is required", nameof(connectionStringName));

        _connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' not found in configuration");
        _embeddings = embeddings;
        _httpClientFactory = httpClientFactory;
        _hostApiBaseUrl = hostApiBaseUrl;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    // ─── SearchEntities ──────────────────────────────────────────────────

    public async Task<string> SearchEntitiesAsync(ScopedSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();

        var sw = Stopwatch.StartNew();
        try
        {
            var embedding = await GenerateEmbeddingAsync(request.Query);

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            var schema = GraphRagSchemaInitializer.SchemaName;
            var scopeFilter = BuildScopeInClause("e", request.Scopes);

            var sql = new StringBuilder();
            sql.AppendLine($"""
                SELECT TOP(@limit)
                    e.EntityId, e.CanonicalEntityId, e.Name, e.EntityType, e.ScopeKey, e.Description, e.Content, e.Metadata,
                    e.CreatedAt, e.UpdatedAt,
                    VECTOR_DISTANCE('cosine', e.Embedding, CAST(@queryVector AS VECTOR(1536))) AS Distance,
                    d.Name AS DomainName, d.Description AS DomainDescription,
                    cat.Name AS CategoryName, cat.Description AS CategoryDescription
                FROM {schema}.KnowledgeEntity e
                LEFT JOIN {schema}.BelongsTo bt_ec ON e.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = e.ScopeKey
                LEFT JOIN {schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
                LEFT JOIN {schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
                LEFT JOIN {schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
                WHERE e.Embedding IS NOT NULL
                  AND {scopeFilter}
                """);

            if (!string.IsNullOrEmpty(request.EntityTypeFilter))
                sql.AppendLine("  AND e.EntityType = @entityType");
            if (!string.IsNullOrEmpty(request.DomainFilter))
                sql.AppendLine("  AND d.Name = @domainFilter");

            sql.AppendLine("ORDER BY Distance;");

            await using var command = new SqlCommand(sql.ToString(), connection);
            command.Parameters.AddWithValue("@limit", request.Limit);
            AddVectorParameter(command, "@queryVector", embedding);
            AddScopeParameters(command, request.Scopes);

            if (!string.IsNullOrEmpty(request.EntityTypeFilter))
                command.Parameters.AddWithValue("@entityType", request.EntityTypeFilter);
            if (!string.IsNullOrEmpty(request.DomainFilter))
                command.Parameters.AddWithValue("@domainFilter", request.DomainFilter);

            var results = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var domain = reader.IsDBNull(reader.GetOrdinal("DomainName"))
                    ? null : reader.GetString(reader.GetOrdinal("DomainName"));
                var domainDescription = reader.IsDBNull(reader.GetOrdinal("DomainDescription"))
                    ? null : reader.GetString(reader.GetOrdinal("DomainDescription"));
                var category = reader.IsDBNull(reader.GetOrdinal("CategoryName"))
                    ? null : reader.GetString(reader.GetOrdinal("CategoryName"));
                var categoryDescription = reader.IsDBNull(reader.GetOrdinal("CategoryDescription"))
                    ? null : reader.GetString(reader.GetOrdinal("CategoryDescription"));

                results.Add(new
                {
                    entityId = reader.GetGuid(reader.GetOrdinal("EntityId")),
                    canonicalEntityId = reader.GetGuid(reader.GetOrdinal("CanonicalEntityId")),
                    name = reader.GetString(reader.GetOrdinal("Name")),
                    entityType = reader.GetString(reader.GetOrdinal("EntityType")),
                    scope = reader.GetString(reader.GetOrdinal("ScopeKey")),
                    description = reader.IsDBNull(reader.GetOrdinal("Description"))
                        ? null : reader.GetString(reader.GetOrdinal("Description")),
                    content = reader.IsDBNull(reader.GetOrdinal("Content"))
                        ? null : reader.GetString(reader.GetOrdinal("Content")),
                    metadata = reader.IsDBNull(reader.GetOrdinal("Metadata"))
                        ? null : reader.GetString(reader.GetOrdinal("Metadata")),
                    distance = reader.GetDouble(reader.GetOrdinal("Distance")),
                    domain,
                    domainDescription,
                    category,
                    categoryDescription,
                    provenance = BuildProvenance(domain, category)
                });
            }

            sw.Stop();
            await _audit.RecordSearchAsync(
                query: request.Query,
                scopes: request.Scopes,
                limit: request.Limit,
                resultCount: results.Count,
                durationMs: sw.ElapsedMilliseconds,
                searchKind: "Entities",
                ct: ct);

            return results.Count == 0
                ? "No matching entities found."
                : JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchEntities failed for query: {Query}", request.Query);
            return $"Error searching entities: {ex.Message}";
        }
    }

    // ─── SearchChunks ────────────────────────────────────────────────────

    public async Task<string> SearchChunksAsync(ScopedSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();

        var sw = Stopwatch.StartNew();
        try
        {
            var embedding = await GenerateEmbeddingAsync(request.Query);

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            var schema = GraphRagSchemaInitializer.SchemaName;
            // Chunks carry a denormalized ScopeKey, so the filter is on the
            // chunk row itself — no join needed just to check scope.
            var scopeFilter = BuildScopeInClause("c", request.Scopes);

            var sql = new StringBuilder();
            sql.AppendLine($"""
                SELECT TOP(@limit)
                    c.ChunkId, c.EntityId, c.ScopeKey, c.Content, c.ChunkIndex, c.Metadata,
                    e.CanonicalEntityId, e.Name AS EntityName, e.EntityType, e.Description AS EntityDescription,
                    d.Name AS DomainName, d.Description AS DomainDescription,
                    cat.Name AS CategoryName, cat.Description AS CategoryDescription,
                    VECTOR_DISTANCE('cosine', c.Embedding, CAST(@queryVector AS VECTOR(1536))) AS Distance
                FROM {schema}.KnowledgeChunk c
                INNER JOIN {schema}.KnowledgeEntity e ON c.EntityId = e.EntityId
                LEFT JOIN {schema}.BelongsTo bt_ec ON e.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = e.ScopeKey
                LEFT JOIN {schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
                LEFT JOIN {schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
                LEFT JOIN {schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
                WHERE c.Embedding IS NOT NULL
                  AND {scopeFilter}
                """);

            if (!string.IsNullOrEmpty(request.EntityTypeFilter))
                sql.AppendLine("  AND e.EntityType = @entityType");
            if (!string.IsNullOrEmpty(request.DomainFilter))
                sql.AppendLine("  AND d.Name = @domainFilter");

            sql.AppendLine("ORDER BY Distance;");

            await using var command = new SqlCommand(sql.ToString(), connection);
            command.Parameters.AddWithValue("@limit", request.Limit);
            AddVectorParameter(command, "@queryVector", embedding);
            AddScopeParameters(command, request.Scopes);

            if (!string.IsNullOrEmpty(request.EntityTypeFilter))
                command.Parameters.AddWithValue("@entityType", request.EntityTypeFilter);
            if (!string.IsNullOrEmpty(request.DomainFilter))
                command.Parameters.AddWithValue("@domainFilter", request.DomainFilter);

            var results = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var domain = reader.IsDBNull(reader.GetOrdinal("DomainName"))
                    ? null : reader.GetString(reader.GetOrdinal("DomainName"));
                var domainDescription = reader.IsDBNull(reader.GetOrdinal("DomainDescription"))
                    ? null : reader.GetString(reader.GetOrdinal("DomainDescription"));
                var category = reader.IsDBNull(reader.GetOrdinal("CategoryName"))
                    ? null : reader.GetString(reader.GetOrdinal("CategoryName"));
                var categoryDescription = reader.IsDBNull(reader.GetOrdinal("CategoryDescription"))
                    ? null : reader.GetString(reader.GetOrdinal("CategoryDescription"));

                results.Add(new
                {
                    chunkId = reader.GetGuid(reader.GetOrdinal("ChunkId")),
                    entityId = reader.GetGuid(reader.GetOrdinal("EntityId")),
                    canonicalEntityId = reader.GetGuid(reader.GetOrdinal("CanonicalEntityId")),
                    scope = reader.GetString(reader.GetOrdinal("ScopeKey")),
                    entityName = reader.GetString(reader.GetOrdinal("EntityName")),
                    entityType = reader.GetString(reader.GetOrdinal("EntityType")),
                    entityDescription = reader.IsDBNull(reader.GetOrdinal("EntityDescription"))
                        ? null : reader.GetString(reader.GetOrdinal("EntityDescription")),
                    content = reader.GetString(reader.GetOrdinal("Content")),
                    chunkIndex = reader.GetInt32(reader.GetOrdinal("ChunkIndex")),
                    metadata = reader.IsDBNull(reader.GetOrdinal("Metadata"))
                        ? null : reader.GetString(reader.GetOrdinal("Metadata")),
                    distance = reader.GetDouble(reader.GetOrdinal("Distance")),
                    domain,
                    domainDescription,
                    category,
                    categoryDescription,
                    provenance = BuildProvenance(domain, category)
                });
            }

            sw.Stop();
            await _audit.RecordSearchAsync(
                query: request.Query,
                scopes: request.Scopes,
                limit: request.Limit,
                resultCount: results.Count,
                durationMs: sw.ElapsedMilliseconds,
                searchKind: "Chunks",
                ct: ct);

            return results.Count == 0
                ? "No matching content chunks found."
                : JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchChunks failed for query: {Query}", request.Query);
            return $"Error searching chunks: {ex.Message}";
        }
    }

    // ─── SearchRelationships ─────────────────────────────────────────────

    public async Task<string> SearchRelationshipsAsync(
        ScopedRelationshipRequest request, CancellationToken ct = default)
    {
        request.Validate();

        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            var schema = GraphRagSchemaInitializer.SchemaName;
            var sql = BuildScopedMatchQuery(
                schema, request.Depth, request.RelationshipTypeFilter, request.Scopes, request.SourceScope);

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@entityName", request.EntityName);
            command.Parameters.AddWithValue("@entityType", request.EntityType);
            if (request.SourceScope is not null)
                command.Parameters.AddWithValue("@sourceScope", request.SourceScope);

            if (!string.IsNullOrEmpty(request.RelationshipTypeFilter))
                command.Parameters.AddWithValue("@relType", request.RelationshipTypeFilter);

            AddScopeParameters(command, request.Scopes);

            var results = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }

            sw.Stop();
            await _audit.RecordSearchAsync(
                query: $"{request.EntityName} ({request.EntityType})",
                scopes: request.Scopes,
                limit: request.Depth,
                resultCount: results.Count,
                durationMs: sw.ElapsedMilliseconds,
                searchKind: "Relationships",
                ct: ct);

            return results.Count == 0
                ? $"No in-scope relationships found for entity '{request.EntityName}'."
                : JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchRelationships failed for entity: {Entity}", request.EntityName);
            return $"Error searching relationships: {ex.Message}";
        }
    }

    // ─── HybridSearch ────────────────────────────────────────────────────

    public async Task<string> HybridSearchAsync(
        ScopedSearchRequest request,
        int graphDepth = 1,
        int vectorLimit = 5,
        CancellationToken ct = default)
    {
        request.Validate();
        graphDepth = Math.Clamp(graphDepth, 1, 3);
        vectorLimit = Math.Clamp(vectorLimit, 1, 20);

        // CorrelationId ties the hybrid wrapper row to the sub-search rows
        // emitted by SearchEntities/SearchChunks/SearchRelationships, so an
        // admin UI can collapse them into a single user-visible "search".
        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();

        try
        {
            // Phase 1: entity + chunk searches. Reuse SearchEntities/Chunks so
            // scope enforcement and provenance are applied the same way.
            var phase1Request = request with { Limit = vectorLimit };
            var entitiesJson = await SearchEntitiesAsync(phase1Request, ct);
            var chunksJson = await SearchChunksAsync(phase1Request, ct);

            var entities = ParseArrayOrEmpty(entitiesJson);
            var chunks = ParseArrayOrEmpty(chunksJson);

            if (entities.Count == 0 && chunks.Count == 0)
                return "No matching entities or content found within the allowed scopes.";

            // Phase 2: collect every entity name that showed up in either
            // result set and expand graph relationships for each, filtered
            // by the same scope set.
            var expansionTargets = new HashSet<(string Name, string Type, string Scope)>();

            foreach (var e in entities)
            {
                if (e.TryGetValue("name", out var n) && e.TryGetValue("entityType", out var t) && e.TryGetValue("scope", out var s)
                    && n?.ToString() is { } name && t?.ToString() is { } type && s?.ToString() is { } scope)
                {
                    expansionTargets.Add((name, type, scope));
                }
            }
            foreach (var c in chunks)
            {
                if (c.TryGetValue("entityName", out var n) && c.TryGetValue("entityType", out var t) && c.TryGetValue("scope", out var s)
                    && n?.ToString() is { } name && t?.ToString() is { } type && s?.ToString() is { } scope)
                {
                    expansionTargets.Add((name, type, scope));
                }
            }

            var relationshipsByEntity = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var (name, type, scope) in expansionTargets)
            {
                var relReq = new ScopedRelationshipRequest(
                    EntityName: name,
                    EntityType: type,
                    Scopes: request.Scopes,
                    RelationshipTypeFilter: null,
                    Depth: graphDepth,
                    SourceScope: scope);

                var relJson = await SearchRelationshipsAsync(relReq, ct);
                var rels = ParseArrayOrEmpty(relJson);
                if (rels.Count > 0)
                    relationshipsByEntity[$"{scope}|{type}|{name}"] = rels;
            }

            var hybrid = new Dictionary<string, object?>
            {
                ["entities"] = entities,
                ["canonicalEntities"] = GroupEntitiesByCanonicalIdentity(entities),
                ["chunks"] = chunks,
                ["relationships"] = relationshipsByEntity
            };

            sw.Stop();
            var totalRelationships = relationshipsByEntity.Values.Sum(v => v.Count);
            await _audit.RecordSearchAsync(
                query: request.Query,
                scopes: request.Scopes,
                limit: vectorLimit,
                resultCount: entities.Count + chunks.Count + totalRelationships,
                durationMs: sw.ElapsedMilliseconds,
                searchKind: "Hybrid",
                correlationId: correlationId,
                ct: ct);

            return JsonSerializer.Serialize(hybrid, JsonOptions);
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HybridSearch failed for query: {Query}", request.Query);
            return $"Error in hybrid search: {ex.Message}";
        }
    }

    // ─── DeepSearch ──────────────────────────────────────────────────────

    public async Task<string> DeepSearchAsync(
        ScopedSearchRequest request,
        int graphDepth = 2,
        int vectorLimit = 10,
        int maxIterations = 2,
        CancellationToken ct = default)
    {
        request.Validate();
        graphDepth = Math.Clamp(graphDepth, 1, 3);
        vectorLimit = Math.Clamp(vectorLimit, 1, 20);
        maxIterations = Math.Clamp(maxIterations, 1, 3);

        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();

        try
        {
            var entitiesByKey = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var chunksByKey = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var relationshipsByKey = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var domainsByName = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var categoriesByName = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            var iterationSummaries = new List<object>();
            var expandedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frontier = new HashSet<(string Name, string Type)>();
            var stopReason = "maxIterations";

            for (var iteration = 1; iteration <= maxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                var iterationEntities = 0;
                var iterationChunks = 0;
                var iterationRelationships = 0;
                var newTargets = new HashSet<(string Name, string Type)>();

                if (iteration == 1)
                {
                    var phaseRequest = request with { Limit = vectorLimit };
                    var entitiesJson = await SearchEntitiesAsync(phaseRequest, ct);
                    var chunksJson = await SearchChunksAsync(phaseRequest, ct);

                    var entityRows = ParseArrayOrEmpty(entitiesJson);
                    foreach (var entity in entityRows)
                    {
                        if (TryAddEntity(entity, entitiesByKey))
                            iterationEntities++;
                        AddTaxonomy(entity, domainsByName, categoriesByName);
                        if (TryGetEntityTarget(entity, out var target))
                            newTargets.Add(target);
                    }

                    var chunkRows = ParseArrayOrEmpty(chunksJson);
                    foreach (var chunk in chunkRows)
                    {
                        if (TryAddChunk(chunk, chunksByKey))
                            iterationChunks++;
                        AddTaxonomy(chunk, domainsByName, categoriesByName);
                        if (TryGetChunkTarget(chunk, out var target))
                            newTargets.Add(target);
                    }
                }
                else
                {
                    newTargets.UnionWith(frontier);
                }

                frontier.Clear();

                foreach (var (name, type) in newTargets)
                {
                    var targetKey = EntityTargetKey(name, type);
                    if (!expandedTargets.Add(targetKey))
                        continue;

                    var relReq = new ScopedRelationshipRequest(
                        EntityName: name,
                        EntityType: type,
                        Scopes: request.Scopes,
                        RelationshipTypeFilter: null,
                        Depth: graphDepth);

                    var relJson = await SearchRelationshipsAsync(relReq, ct);
                    var relationshipRows = ParseArrayOrEmpty(relJson);
                    foreach (var relationship in relationshipRows)
                    {
                        if (TryAddRelationship(relationship, relationshipsByKey))
                            iterationRelationships++;

                        foreach (var relatedTarget in ExtractRelationshipTargets(relationship))
                        {
                            if (!expandedTargets.Contains(EntityTargetKey(relatedTarget.Name, relatedTarget.Type)))
                                frontier.Add(relatedTarget);

                            AddRelationshipEntityStub(relatedTarget, relationship, entitiesByKey);
                        }
                    }
                }

                iterationSummaries.Add(new
                {
                    iteration,
                    query = request.Query,
                    expandedTargetCount = newTargets.Count,
                    newEntityCount = iterationEntities,
                    newChunkCount = iterationChunks,
                    newRelationshipCount = iterationRelationships,
                    nextFrontierCount = frontier.Count
                });

                if (iteration > 1 && iterationEntities == 0 && iterationChunks == 0 && iterationRelationships == 0)
                {
                    stopReason = "noNewEvidence";
                    break;
                }

                if (frontier.Count == 0)
                {
                    stopReason = "noNewEvidence";
                    break;
                }
            }

            sw.Stop();

            var deepSearch = new Dictionary<string, object?>
            {
                ["query"] = request.Query,
                ["scopes"] = request.Scopes,
                ["options"] = new
                {
                    graphDepth,
                    vectorLimit,
                    maxIterations,
                    entityTypeFilter = request.EntityTypeFilter,
                    domainFilter = request.DomainFilter
                },
                ["stopReason"] = stopReason,
                ["durationMs"] = sw.ElapsedMilliseconds,
                ["entities"] = entitiesByKey.Values.ToList(),
                ["canonicalEntities"] = GroupEntitiesByCanonicalIdentity(entitiesByKey.Values),
                ["chunks"] = chunksByKey.Values.ToList(),
                ["relationships"] = relationshipsByKey.Values.ToList(),
                ["domains"] = domainsByName.Values.ToList(),
                ["categories"] = categoriesByName.Values.ToList(),
                ["iterations"] = iterationSummaries,
                ["coverageSummary"] = new
                {
                    entityCount = entitiesByKey.Count,
                    chunkCount = chunksByKey.Count,
                    relationshipCount = relationshipsByKey.Count,
                    domainCount = domainsByName.Count,
                    categoryCount = categoriesByName.Count,
                    expandedEntityCount = expandedTargets.Count
                }
            };

            await _audit.RecordSearchAsync(
                query: request.Query,
                scopes: request.Scopes,
                limit: vectorLimit,
                resultCount: entitiesByKey.Count + chunksByKey.Count + relationshipsByKey.Count,
                durationMs: sw.ElapsedMilliseconds,
                searchKind: "DeepSearch",
                correlationId: correlationId,
                ct: ct);

            return JsonSerializer.Serialize(deepSearch, JsonOptions);
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeepSearch failed for query: {Query}", request.Query);
            return $"Error in deep search: {ex.Message}";
        }
    }

    // ─── Internal helpers ────────────────────────────────────────────────

    private static List<object> GroupEntitiesByCanonicalIdentity(
        IEnumerable<Dictionary<string, object?>> entities)
    {
        return entities
            .GroupBy(entity =>
                GetString(entity, "canonicalEntityId")
                ?? $"uncanonicalized:{GetString(entity, "entityType")}:{GetString(entity, "name")}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return (object)new
                {
                    canonicalEntityId = GetString(first, "canonicalEntityId"),
                    name = GetString(first, "name"),
                    entityType = GetString(first, "entityType"),
                    scopedViews = group.ToList()
                };
            })
            .ToList();
    }

    private async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (_embeddings is not null)
        {
            var result = await _embeddings.GetEmbeddings(text);
            return result.Vector.ToArray();
        }

        if (_httpClientFactory is not null && !string.IsNullOrWhiteSpace(_hostApiBaseUrl))
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(_hostApiBaseUrl.TrimEnd('/'));
            var response = await client.PostAsJsonAsync("/fabrcoreapi/Embeddings", new { Text = text });
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var vectorElement = doc.RootElement.GetProperty("vector");
            var vector = new float[vectorElement.GetArrayLength()];
            int idx = 0;
            foreach (var item in vectorElement.EnumerateArray())
                vector[idx++] = item.GetSingle();
            return vector;
        }

        throw new InvalidOperationException(
            "No embeddings provider available. Either register IEmbeddings via AddFabrCoreServer() " +
            "or configure FabrCoreHostUrl + IHttpClientFactory for remote embeddings.");
    }

    /// <summary>
    /// Builds <c>alias.ScopeKey IN (@__scope0, @__scope1, ...)</c>. The
    /// parameter names are fixed so they can't collide with caller-supplied
    /// parameters (which are always named <c>@paramName</c> without the
    /// double-underscore prefix).
    /// </summary>
    private static string BuildScopeInClause(string alias, IReadOnlyList<string> scopes)
    {
        var names = string.Join(", ", Enumerable.Range(0, scopes.Count).Select(i => $"@__scope{i}"));
        return $"{alias}.ScopeKey IN ({names})";
    }

    /// <summary>
    /// Adds the <c>@__scope0</c>, <c>@__scope1</c>, ... parameters that back
    /// the scope IN-clause filters. No priority multipliers are emitted —
    /// all listed scopes are treated on equal footing and ranking is driven
    /// purely by raw vector distance.
    /// </summary>
    private static void AddScopeParameters(SqlCommand cmd, IReadOnlyList<string> scopes)
    {
        for (var i = 0; i < scopes.Count; i++)
            cmd.Parameters.AddWithValue($"@__scope{i}", scopes[i]);
    }

    private static void AddVectorParameter(SqlCommand cmd, string name, float[] embedding)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbTypeExtensions.Vector)
        {
            Value = new SqlVector<float>(embedding)
        });
    }

    private static string? BuildProvenance(string? domain, string? category)
    {
        if (domain is not null && category is not null) return $"{domain} > {category}";
        if (domain is not null) return domain;
        return null;
    }

    private static bool TryAddEntity(
        Dictionary<string, object?> row,
        Dictionary<string, Dictionary<string, object?>> entitiesByKey)
    {
        if (!TryGetEntityTarget(row, out var target))
            return false;

        var scope = GetString(row, "scope") ?? GetString(row, "sourceScope") ?? GetString(row, "targetScope");
        var key = EntityKey(target.Name, target.Type, scope);
        if (entitiesByKey.ContainsKey(key))
            return false;

        entitiesByKey[key] = row;
        return true;
    }

    private static bool TryAddChunk(
        Dictionary<string, object?> row,
        Dictionary<string, Dictionary<string, object?>> chunksByKey)
    {
        var id = GetString(row, "chunkId");
        if (string.IsNullOrWhiteSpace(id))
            id = $"{GetString(row, "entityName")}:{GetString(row, "chunkIndex")}:{GetString(row, "scope")}";

        if (chunksByKey.ContainsKey(id))
            return false;

        chunksByKey[id] = row;
        return true;
    }

    private static bool TryAddRelationship(
        Dictionary<string, object?> row,
        Dictionary<string, Dictionary<string, object?>> relationshipsByKey)
    {
        var key = StableRowKey(row);
        if (relationshipsByKey.ContainsKey(key))
            return false;

        relationshipsByKey[key] = row;
        return true;
    }

    private static void AddTaxonomy(
        Dictionary<string, object?> row,
        Dictionary<string, Dictionary<string, object?>> domainsByName,
        Dictionary<string, Dictionary<string, object?>> categoriesByName)
    {
        var domain = GetString(row, "domain");
        if (!string.IsNullOrWhiteSpace(domain) && !domainsByName.ContainsKey(domain))
        {
            domainsByName[domain] = new Dictionary<string, object?>
            {
                ["name"] = domain,
                ["description"] = GetString(row, "domainDescription")
            };
        }

        var category = GetString(row, "category");
        if (!string.IsNullOrWhiteSpace(category) && !categoriesByName.ContainsKey(category))
        {
            categoriesByName[category] = new Dictionary<string, object?>
            {
                ["name"] = category,
                ["description"] = GetString(row, "categoryDescription"),
                ["domain"] = domain
            };
        }
    }

    private static void AddRelationshipEntityStub(
        (string Name, string Type) target,
        Dictionary<string, object?> relationship,
        Dictionary<string, Dictionary<string, object?>> entitiesByKey)
    {
        var scope = GetScopeForRelationshipTarget(target, relationship);
        var key = EntityKey(target.Name, target.Type, scope);
        if (entitiesByKey.ContainsKey(key))
            return;

        entitiesByKey[key] = new Dictionary<string, object?>
        {
            ["name"] = target.Name,
            ["entityType"] = target.Type,
            ["scope"] = scope,
            ["source"] = "relationshipExpansion"
        };
    }

    private static bool TryGetEntityTarget(
        Dictionary<string, object?> row,
        out (string Name, string Type) target)
    {
        var name = GetString(row, "name");
        var type = GetString(row, "entityType");
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type))
        {
            target = (name, type);
            return true;
        }

        target = default;
        return false;
    }

    private static bool TryGetChunkTarget(
        Dictionary<string, object?> row,
        out (string Name, string Type) target)
    {
        var name = GetString(row, "entityName");
        var type = GetString(row, "entityType");
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type))
        {
            target = (name, type);
            return true;
        }

        target = default;
        return false;
    }

    private static IEnumerable<(string Name, string Type)> ExtractRelationshipTargets(Dictionary<string, object?> row)
    {
        foreach (var (nameKey, typeKey) in new[]
        {
            ("SourceName", "SourceType"),
            ("TargetName", "TargetType"),
            ("Hop1Name", "Hop1Type"),
            ("Hop2Name", "Hop2Type"),
            ("Hop3Name", "Hop3Type")
        })
        {
            var name = GetString(row, nameKey);
            var type = GetString(row, typeKey);
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type))
                yield return (name, type);
        }
    }

    private static string? GetScopeForRelationshipTarget(
        (string Name, string Type) target,
        Dictionary<string, object?> relationship)
    {
        foreach (var (nameKey, typeKey, scopeKey) in new[]
        {
            ("SourceName", "SourceType", "SourceScope"),
            ("TargetName", "TargetType", "TargetScope"),
            ("Hop1Name", "Hop1Type", "Hop1Scope"),
            ("Hop2Name", "Hop2Type", "Hop2Scope"),
            ("Hop3Name", "Hop3Type", "Hop3Scope")
        })
        {
            if (string.Equals(GetString(relationship, nameKey), target.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetString(relationship, typeKey), target.Type, StringComparison.OrdinalIgnoreCase))
            {
                return GetString(relationship, scopeKey);
            }
        }

        return null;
    }

    private static string EntityTargetKey(string name, string type) => $"{type}|{name}";

    private static string EntityKey(string name, string type, string? scope)
        => $"{scope ?? ""}|{type}|{name}";

    private static string StableRowKey(Dictionary<string, object?> row)
    {
        var parts = row
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}");
        return string.Join("|", parts);
    }

    private static string? GetString(Dictionary<string, object?> row, string key)
    {
        return row.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;
    }

    /// <summary>
    /// Builds a scope-filtered MATCH query for relationship traversal. Uses a
    /// CTE to separate the graph MATCH from the scope join (SQL Server does
    /// not allow scope filters directly inside a MATCH clause). Every endpoint
    /// entity is scope-checked so a job-ops caller cannot see an edge pointing
    /// at a job-ops-manager entity even if the edge itself was authored in a
    /// scope the caller is allowed to see.
    /// </summary>
    private static string BuildScopedMatchQuery(
        string schema, int depth, string? relationshipTypeFilter, IReadOnlyList<string> scopes, string? sourceScope)
    {
        var scopeList = string.Join(", ", Enumerable.Range(0, scopes.Count).Select(i => $"@__scope{i}"));
        var relFilter = string.IsNullOrEmpty(relationshipTypeFilter)
            ? ""
            : " AND r.RelationshipType = @relType";
        var sourceScopeFilter = sourceScope is null ? "" : "AND e1.ScopeKey = @sourceScope";

        if (depth == 1)
        {
            return $"""
                SELECT
                    e1.Name AS SourceName, e1.EntityType AS SourceType, e1.ScopeKey AS SourceScope,
                    r.RelationshipType, r.Description AS RelationshipDescription, r.Weight,
                    e2.Name AS TargetName, e2.EntityType AS TargetType,
                    e2.ScopeKey AS TargetScope, e2.Description AS TargetDescription
                FROM {schema}.KnowledgeEntity e1, {schema}.KnowledgeRelationship r, {schema}.KnowledgeEntity e2
                WHERE MATCH(e1-(r)->e2)
                  AND e1.Name = @entityName AND e1.EntityType = @entityType
                  {sourceScopeFilter}
                  AND r.ScopeKey = e1.ScopeKey
                  AND e1.ScopeKey IN ({scopeList})
                  AND e2.ScopeKey IN ({scopeList})
                  {relFilter};
                """;
        }

        if (depth == 2)
        {
            var rel2Filter = string.IsNullOrEmpty(relationshipTypeFilter)
                ? ""
                : " AND (r1.RelationshipType = @relType OR r2.RelationshipType = @relType)";

            return $"""
                SELECT
                    e1.Name AS SourceName, e1.EntityType AS SourceType, e1.ScopeKey AS SourceScope,
                    r1.RelationshipType AS Rel1Type, r1.Weight AS Rel1Weight,
                    e2.Name AS Hop1Name, e2.EntityType AS Hop1Type, e2.ScopeKey AS Hop1Scope,
                    r2.RelationshipType AS Rel2Type, r2.Weight AS Rel2Weight,
                    e3.Name AS Hop2Name, e3.EntityType AS Hop2Type, e3.ScopeKey AS Hop2Scope,
                    e3.Description AS Hop2Description
                FROM {schema}.KnowledgeEntity e1,
                     {schema}.KnowledgeRelationship r1, {schema}.KnowledgeEntity e2,
                     {schema}.KnowledgeRelationship r2, {schema}.KnowledgeEntity e3
                WHERE MATCH(e1-(r1)->e2-(r2)->e3)
                  AND e1.Name = @entityName AND e1.EntityType = @entityType
                  {sourceScopeFilter}
                  AND r1.ScopeKey = e1.ScopeKey AND r2.ScopeKey = e1.ScopeKey
                  AND e1.ScopeKey IN ({scopeList})
                  AND e2.ScopeKey IN ({scopeList})
                  AND e3.ScopeKey IN ({scopeList})
                  {rel2Filter};
                """;
        }

        // depth == 3
        var rel3Filter = string.IsNullOrEmpty(relationshipTypeFilter)
            ? ""
            : " AND (r1.RelationshipType = @relType OR r2.RelationshipType = @relType OR r3.RelationshipType = @relType)";

        return $"""
            SELECT
                e1.Name AS SourceName, e1.EntityType AS SourceType, e1.ScopeKey AS SourceScope,
                r1.RelationshipType AS Rel1Type,
                e2.Name AS Hop1Name, e2.EntityType AS Hop1Type, e2.ScopeKey AS Hop1Scope,
                r2.RelationshipType AS Rel2Type,
                e3.Name AS Hop2Name, e3.EntityType AS Hop2Type, e3.ScopeKey AS Hop2Scope,
                r3.RelationshipType AS Rel3Type,
                e4.Name AS Hop3Name, e4.EntityType AS Hop3Type, e4.ScopeKey AS Hop3Scope,
                e4.Description AS Hop3Description
            FROM {schema}.KnowledgeEntity e1,
                 {schema}.KnowledgeRelationship r1, {schema}.KnowledgeEntity e2,
                 {schema}.KnowledgeRelationship r2, {schema}.KnowledgeEntity e3,
                 {schema}.KnowledgeRelationship r3, {schema}.KnowledgeEntity e4
            WHERE MATCH(e1-(r1)->e2-(r2)->e3-(r3)->e4)
              AND e1.Name = @entityName AND e1.EntityType = @entityType
              {sourceScopeFilter}
              AND r1.ScopeKey = e1.ScopeKey AND r2.ScopeKey = e1.ScopeKey AND r3.ScopeKey = e1.ScopeKey
              AND e1.ScopeKey IN ({scopeList})
              AND e2.ScopeKey IN ({scopeList})
              AND e3.ScopeKey IN ({scopeList})
              AND e4.ScopeKey IN ({scopeList})
              {rel3Filter};
            """;
    }

    /// <summary>
    /// Parses a JSON array string produced by one of the Search* methods into
    /// a list of property dictionaries. Returns an empty list if the JSON is
    /// null, whitespace, an error message, or a "No ... found" string.
    /// </summary>
    private static List<Dictionary<string, object?>> ParseArrayOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        var trimmed = json.TrimStart();
        if (!trimmed.StartsWith('[')) return new();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<Dictionary<string, object?>>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var row = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    row[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
                list.Add(row);
            }
            return list;
        }
        catch
        {
            return new();
        }
    }
}
