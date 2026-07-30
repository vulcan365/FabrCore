using System.ComponentModel;
using System.Text.Json;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// Ingestion plugin. Scope is read from <c>AllowedScopes</c> config at init
/// time and baked into every write operation. The LLM never sees a scope
/// parameter — it cannot choose or change the scope boundary.
///
/// Rules enforced at the SQL layer:
/// <list type="bullet">
///   <item><b>Uniqueness</b> is <c>(Name, EntityType, ScopeKey)</c>.
///         The same document name under a different scope is a DIFFERENT row
///         — but <see cref="DocumentExistsAsync"/> is scope-agnostic so
///         <see cref="IngestDocumentAsync"/> refuses cross-scope duplicates.</item>
///   <item><b>Chunks</b> denormalize their parent entity's <c>ScopeKey</c> so
///         chunk search is a single-table filter.</item>
///   <item><b>Relationships</b> must connect two entities in the SAME scope.
///         Cross-scope edges are refused.</item>
/// </list>
/// </summary>
[PluginAlias("graph-rag-ingest")]
public class GraphRagIngestPlugin : GraphRagSearchPlugin
{
    protected override string PluginAlias => "graph-rag-ingest";

    private IKnowledgeIngestionService? _ingestionService;

    public override Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _ingestionService = serviceProvider.GetService<IKnowledgeIngestionService>();
        return base.InitializeAsync(config, serviceProvider);
    }

    /// <summary>
    /// Returns the configured scope key for write operations, or throws if
    /// not configured. Uses the first entry from AllowedScopes.
    /// </summary>
    private string GetScopeKey()
    {
        var scopes = GetAllowedScopes();
        return scopes[0];
    }

    [Description("Add a new entity to the knowledge graph. The entity's content is embedded for vector search.")]
    public Task<string> AddEntity(
        [Description("The name of the entity")] string name,
        [Description("The type/category of the entity (e.g. 'Person', 'Concept', 'Document', 'Event')")] string entityType,
        [Description("A brief description of the entity")] string description,
        [Description("The full content/text associated with this entity")] string? content = null,
        [Description("Optional JSON metadata string")] string? metadata = null)
        => AddEntityInScope(GetScopeKey(), name, entityType, description, content, metadata);

    private async Task<string> AddEntityInScope(
        string scopeKey,
        string name,
        string entityType,
        string description,
        string? content = null,
        string? metadata = null)
    {
        try
        {
            var textToEmbed = $"{name}. {description}" + (content is not null ? $" {content}" : "");
            float[]? embedding = null;
            try
            {
                embedding = await GenerateEmbeddingAsync(textToEmbed);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to generate embedding for entity '{Name}', inserting without embedding", name);
            }

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await ScopeRegistryStore.EnsureExistsAsync(connection, transaction: null, scopeKey);
            var canonicalEntityId = await CanonicalEntityStore.GetOrCreateAsync(
                connection, transaction: null, name, entityType);

            var schema = GraphRagSchemaInitializer.SchemaName;

            // MERGE key: (Name, EntityType, ScopeKey). Two rows with the same
            // name+type in different scopes coexist as distinct entities. A
            // repeated ingestion under the SAME scope updates in place.
            var sql = $"""
                MERGE {schema}.KnowledgeEntity AS target
                USING (SELECT @canonicalEntityId AS CanonicalEntityId, @name AS Name, @entityType AS EntityType, @scopeKey AS ScopeKey) AS source
                ON target.Name = source.Name
                   AND target.EntityType = source.EntityType
                   AND target.ScopeKey = source.ScopeKey
                WHEN MATCHED THEN
                    UPDATE SET
                        Description = CASE WHEN @description IS NOT NULL AND LEN(@description) > LEN(ISNULL(target.Description, '')) THEN @description ELSE target.Description END,
                        CanonicalEntityId = source.CanonicalEntityId,
                        Content = CASE WHEN @content IS NOT NULL THEN @content ELSE target.Content END,
                        Embedding = {(embedding is not null ? "CAST(@embedding AS VECTOR(1536))" : "target.Embedding")},
                        Metadata = CASE WHEN @metadata IS NOT NULL THEN @metadata ELSE target.Metadata END,
                        UpdatedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (EntityId, CanonicalEntityId, Name, EntityType, ScopeKey, Description, Content, Embedding, Metadata)
                    VALUES (NEWID(), @canonicalEntityId, @name, @entityType, @scopeKey, @description, @content,
                            {(embedding is not null ? "CAST(@embedding AS VECTOR(1536))" : "NULL")},
                            @metadata)
                OUTPUT INSERTED.EntityId, $action AS MergeAction;
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@canonicalEntityId", canonicalEntityId);
            command.Parameters.AddWithValue("@entityType", entityType);
            command.Parameters.AddWithValue("@scopeKey", scopeKey);
            command.Parameters.AddWithValue("@description", description);
            command.Parameters.AddWithValue("@content", (object?)content ?? DBNull.Value);
            command.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);

            if (embedding is not null)
            {
                command.Parameters.Add(new SqlParameter("@embedding", SqlDbTypeExtensions.Vector)
                {
                    Value = new SqlVector<float>(embedding)
                });
            }

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("AddEntity MERGE did not return a row");

            var entityId = reader.GetGuid(reader.GetOrdinal("EntityId"));
            var action = reader.GetString(reader.GetOrdinal("MergeAction"));
            var isUpdate = string.Equals(action, "UPDATE", StringComparison.OrdinalIgnoreCase);

            Logger.LogInformation("{Action} entity '{Name}' ({Type}) in scope '{Scope}' -> {EntityId}",
                isUpdate ? "Updated" : "Added", name, entityType, scopeKey, entityId);

            return JsonSerializer.Serialize(new
            {
                entityId,
                name,
                entityType,
                scope = scopeKey,
                message = isUpdate ? "Entity already existed and was updated." : "Entity added successfully."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AddEntity failed for '{Name}' (scope '{Scope}')", name, scopeKey);
            return $"Error adding entity: {ex.Message}";
        }
    }

    [Description("Add content chunks for an existing entity. Splits, embeds, and stores each chunk.")]
    public Task<string> AddChunks(
        [Description("The name of the parent entity to attach chunks to")] string entityName,
        [Description("The type of the parent entity")] string entityType,
        [Description("The full content text to chunk and embed")] string content,
        [Description("Approximate characters per chunk (default 500)")] int chunkSize = 500,
        [Description("Characters of overlap between consecutive chunks (default 100, 0 to disable)")] int overlapChars = 100,
        [Description("Optional JSON metadata applied to all chunks")] string? metadata = null)
        => AddChunksInScope(GetScopeKey(), entityName, entityType, content, chunkSize, overlapChars, metadata);

    private async Task<string> AddChunksInScope(
        string scopeKey,
        string entityName,
        string entityType,
        string content,
        int chunkSize = 500,
        int overlapChars = 100,
        string? metadata = null)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;

            // Look up the parent entity restricted to the supplied scope.
            // If the LLM (or a bug) supplies a different scope than the one
            // the entity actually lives in, this returns null and we refuse.
            var lookupSql = $"""
                SELECT EntityId FROM {schema}.KnowledgeEntity
                WHERE Name = @name AND EntityType = @type AND ScopeKey = @scopeKey
                """;

            Guid entityId;
            await using (var cmd = new SqlCommand(lookupSql, connection))
            {
                cmd.Parameters.AddWithValue("@name", entityName);
                cmd.Parameters.AddWithValue("@type", entityType);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                var result = await cmd.ExecuteScalarAsync();
                if (result is null)
                    return $"Error: Entity '{entityName}' ({entityType}) not found in scope '{scopeKey}'. " +
                           "Add the entity first via AddEntity with the same scope.";
                entityId = (Guid)result;
            }

            var chunks = SplitIntoChunks(content, chunkSize, overlapChars);
            if (chunks.Count == 0)
                return "No content to chunk.";

            var insertedCount = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                float[]? embedding = null;
                try
                {
                    embedding = await GenerateEmbeddingAsync(chunks[i]);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to embed chunk {Index} for entity '{Name}'", i, entityName);
                }

                var insertSql = $"""
                    INSERT INTO {schema}.KnowledgeChunk
                        (ChunkId, EntityId, ScopeKey, Content, Embedding, ChunkIndex, Metadata)
                    VALUES (NEWID(), @entityId, @scopeKey, @content,
                            {(embedding is not null ? "CAST(@embedding AS VECTOR(1536))" : "NULL")},
                            @chunkIndex, @metadata);
                    """;

                await using var cmd = new SqlCommand(insertSql, connection);
                cmd.Parameters.AddWithValue("@entityId", entityId);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                cmd.Parameters.AddWithValue("@content", chunks[i]);
                cmd.Parameters.AddWithValue("@chunkIndex", i);
                cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);

                if (embedding is not null)
                {
                    cmd.Parameters.Add(new SqlParameter("@embedding", SqlDbTypeExtensions.Vector)
                    {
                        Value = new SqlVector<float>(embedding)
                    });
                }

                await cmd.ExecuteNonQueryAsync();
                insertedCount++;
            }

            Logger.LogInformation("Added {Count} chunks for entity '{Name}' ({Type}) in scope '{Scope}'",
                insertedCount, entityName, entityType, scopeKey);

            return JsonSerializer.Serialize(new
            {
                entityId,
                entityName,
                scope = scopeKey,
                chunksAdded = insertedCount,
                message = $"Successfully added {insertedCount} content chunks for entity '{entityName}' in scope '{scopeKey}'."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AddChunks failed for entity '{Name}' (scope '{Scope}')", entityName, scopeKey);
            return $"Error adding chunks: {ex.Message}";
        }
    }

    // ─── Programmatic Helpers (called directly by agent, not exposed as LLM tools) ────

    /// <summary>
    /// Checks whether a document entity with the given name exists under
    /// ANY scope. Used by <see cref="IngestDocumentAsync"/> to enforce the
    /// single-scope ingestion rule — a document cannot be re-ingested into a
    /// second scope.
    /// </summary>
    public async Task<bool> DocumentExistsAsync(string documentName)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var sql = $"""
            SELECT COUNT(*) FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeEntity
            WHERE Name = @name AND EntityType = 'Document'
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", documentName);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    /// <summary>
    /// Returns the scope key a document currently lives in, or null if it
    /// doesn't exist. Used to build a helpful error message when refusing a
    /// duplicate ingestion.
    /// </summary>
    public async Task<string?> GetDocumentScopeAsync(string documentName)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var sql = $"""
            SELECT TOP(1) ScopeKey FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeEntity
            WHERE Name = @name AND EntityType = 'Document'
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", documentName);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    private async Task<string?> GetDocumentScopeAsync(string documentName, string scopeKey)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var sql = $"""
            SELECT ScopeKey FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeEntity
            WHERE Name = @name AND EntityType = 'Document' AND ScopeKey = @scopeKey
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", documentName);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// Purges a document. When a <c>SourceDocument</c> row exists for
    /// <paramref name="documentName"/>, delegates to the reference-counting
    /// <see cref="IKnowledgeIngestionService.DeleteDocumentAsync"/> so shared
    /// entities/relationships contributed by other documents are preserved.
    /// For plugin-only documents (no <c>SourceDocument</c> row), falls back to
    /// a simple cascade of the document entity and its chunks.
    /// </summary>
    public async Task<(int EntitiesRemoved, int ChunksRemoved, int RelationshipsRemoved)> PurgeDocumentAsync(
        string documentName, string? scopeKey = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var schema = GraphRagSchemaInitializer.SchemaName;

        // Look up any SourceDocument rows for this name — preferred path.
        var sourceLookupSql = scopeKey is null
            ? $"SELECT DocumentId FROM {schema}.SourceDocument WHERE FileName = @name"
            : $"SELECT DocumentId FROM {schema}.SourceDocument WHERE FileName = @name AND ScopeKey = @scopeKey";

        var sourceDocIds = new List<Guid>();
        await using (var cmd = new SqlCommand(sourceLookupSql, connection))
        {
            cmd.Parameters.AddWithValue("@name", documentName);
            if (scopeKey is not null) cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                sourceDocIds.Add(reader.GetGuid(0));
        }

        if (sourceDocIds.Count > 0 && _ingestionService is not null)
        {
            foreach (var docId in sourceDocIds)
                await _ingestionService.DeleteDocumentAsync(docId);

            Logger.LogInformation(
                "Purged document '{Name}' (scope '{Scope}'): {Count} SourceDocument row(s) deleted via reference-counting service",
                documentName, scopeKey ?? "<all>", sourceDocIds.Count);

            return (sourceDocIds.Count, 0, 0);
        }

        // Fallback: no SourceDocument row (plugin-only path). Remove any
        // standalone document entity + its chunks for the given scope.
        var entityLookupSql = scopeKey is null
            ? $"SELECT EntityId FROM {schema}.KnowledgeEntity WHERE Name = @name AND EntityType = 'Document'"
            : $"SELECT EntityId FROM {schema}.KnowledgeEntity WHERE Name = @name AND EntityType = 'Document' AND ScopeKey = @scopeKey";

        var docEntityIds = new List<Guid>();
        await using (var cmd = new SqlCommand(entityLookupSql, connection))
        {
            cmd.Parameters.AddWithValue("@name", documentName);
            if (scopeKey is not null) cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                docEntityIds.Add(reader.GetGuid(0));
        }

        var totalEntities = 0;
        var totalChunks = 0;
        foreach (var entityId in docEntityIds)
        {
            await using (var cmd = new SqlCommand(
                $"DELETE FROM {schema}.KnowledgeChunk WHERE EntityId = @entityId", connection))
            {
                cmd.Parameters.AddWithValue("@entityId", entityId);
                totalChunks += await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new SqlCommand(
                $"""
                DELETE bt FROM {schema}.BelongsTo bt
                INNER JOIN {schema}.KnowledgeEntity e ON bt.$from_id = e.$node_id OR bt.$to_id = e.$node_id
                WHERE e.EntityId = @entityId
                """, connection))
            {
                cmd.Parameters.AddWithValue("@entityId", entityId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new SqlCommand(
                $"DELETE FROM {schema}.KnowledgeEntity WHERE EntityId = @entityId", connection))
            {
                cmd.Parameters.AddWithValue("@entityId", entityId);
                totalEntities += await cmd.ExecuteNonQueryAsync();
            }
        }

        Logger.LogInformation(
            "Purged plugin-only document '{Name}' (scope '{Scope}'): {Entities} entities, {Chunks} chunks",
            documentName, scopeKey ?? "<all>", totalEntities, totalChunks);

        return (totalEntities, totalChunks, 0);
    }

    /// <summary>
    /// Deterministically ingests a document under the supplied scope. Unlike the
    /// UI's service path, this is a lightweight chunks-only flow — no LLM
    /// extraction, no entity/relationship generation. Used programmatically by
    /// agents that read the scope out of <c>AgentMessage.State</c>.
    /// </summary>
    /// <param name="replaceExisting">
    /// If false (default) and a document with the same name already exists
    /// under any scope, the call throws. If true, any existing document is
    /// purged before re-ingest. Defaults to false for safety.
    /// </param>
    public async Task<(Guid EntityId, int ChunkCount)> IngestDocumentAsync(
        string name,
        string scopeKey,
        string description,
        string markdownContent,
        string? sourceUrl = null,
        bool replaceExisting = false)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey is required", nameof(scopeKey));

        var existingScope = await GetDocumentScopeAsync(name, scopeKey);
        if (existingScope is not null)
        {
            if (!replaceExisting)
            {
                throw new InvalidOperationException(
                    $"Document '{name}' already ingested under scope '{existingScope}'. " +
                    "Pass replaceExisting=true to re-process, or purge first.");
            }

            // Replace only the copy owned by the requested scope.
            await PurgeDocumentAsync(name, scopeKey);
        }

        var metadata = sourceUrl is not null
            ? JsonSerializer.Serialize(new { sourceUrl }, JsonOptions)
            : null;

        // Create the Document entity under the supplied scope
        var entityResult = await AddEntityInScope(scopeKey, name, "Document", description, content: null, metadata: metadata);

        using var doc = JsonDocument.Parse(entityResult);
        if (!doc.RootElement.TryGetProperty("entityId", out var idElem))
            throw new InvalidOperationException($"Failed to create document entity: {entityResult}");

        var entityId = idElem.GetGuid();

        // Chunk and embed the full markdown content under the same scope
        var chunkResult = await AddChunksInScope(scopeKey, name, "Document", markdownContent);

        using var chunkDoc = JsonDocument.Parse(chunkResult);
        var chunkCount = chunkDoc.RootElement.TryGetProperty("chunksAdded", out var countElem)
            ? countElem.GetInt32()
            : 0;

        Logger.LogInformation("Ingested document '{Name}' under scope '{Scope}': entity {EntityId}, {Chunks} chunks",
            name, scopeKey, entityId, chunkCount);

        return (entityId, chunkCount);
    }

    // ─── LLM Tool Methods ───────────────────────────────────────────────

    [Description("Create a directional relationship between two existing entities. Both endpoints must live in the configured scope — cross-scope edges are refused.")]
    public async Task<string> AddRelationship(
        [Description("The name of the source entity")] string fromEntityName,
        [Description("The type of the source entity")] string fromEntityType,
        [Description("The name of the target entity")] string toEntityName,
        [Description("The type of the target entity")] string toEntityType,
        [Description("The type of relationship (e.g. 'RELATED_TO', 'PART_OF', 'CAUSES', 'DEPENDS_ON')")] string relationshipType,
        [Description("Optional description of the relationship")] string? description = null,
        [Description("Relationship weight/strength, 0.0 to 1.0 (default 1.0)")] double weight = 1.0)
    {
        var scopeKey = GetScopeKey();

        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;

            // Verify both endpoints exist UNDER THE SUPPLIED SCOPE. This is
            // where cross-scope edges get refused: if either endpoint lives
            // in a different scope, the COUNT(*) comes back 0 even if the
            // name+type match in another scope.
            var checkSql = $"""
                SELECT
                    (SELECT COUNT(*) FROM {schema}.KnowledgeEntity
                        WHERE Name = @fromName AND EntityType = @fromType AND ScopeKey = @scopeKey) AS FromExists,
                    (SELECT COUNT(*) FROM {schema}.KnowledgeEntity
                        WHERE Name = @toName AND EntityType = @toType AND ScopeKey = @scopeKey) AS ToExists
                """;

            await using (var checkCmd = new SqlCommand(checkSql, connection))
            {
                checkCmd.Parameters.AddWithValue("@fromName", fromEntityName);
                checkCmd.Parameters.AddWithValue("@fromType", fromEntityType);
                checkCmd.Parameters.AddWithValue("@toName", toEntityName);
                checkCmd.Parameters.AddWithValue("@toType", toEntityType);
                checkCmd.Parameters.AddWithValue("@scopeKey", scopeKey);

                await using var reader = await checkCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var fromExists = reader.GetInt32(0) > 0;
                    var toExists = reader.GetInt32(1) > 0;
                    if (!fromExists && !toExists)
                        return $"Error: Neither endpoint found in scope '{scopeKey}' — '{fromEntityName}' ({fromEntityType}) and '{toEntityName}' ({toEntityType}). Cross-scope relationships are not permitted.";
                    if (!fromExists)
                        return $"Error: Source entity '{fromEntityName}' ({fromEntityType}) not found in scope '{scopeKey}'. Cross-scope relationships are not permitted.";
                    if (!toExists)
                        return $"Error: Target entity '{toEntityName}' ({toEntityType}) not found in scope '{scopeKey}'. Cross-scope relationships are not permitted.";
                }
            }

            var sql = $"""
                INSERT INTO {schema}.KnowledgeRelationship ($from_id, $to_id, ScopeKey, RelationshipType, Description, Weight)
                VALUES (
                    (SELECT $node_id FROM {schema}.KnowledgeEntity
                        WHERE Name = @fromName2 AND EntityType = @fromType2 AND ScopeKey = @scopeKey2),
                    (SELECT $node_id FROM {schema}.KnowledgeEntity
                        WHERE Name = @toName2 AND EntityType = @toType2 AND ScopeKey = @scopeKey2),
                    @scopeKey2, @relType, @description, @weight
                );
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@fromName2", fromEntityName);
            command.Parameters.AddWithValue("@fromType2", fromEntityType);
            command.Parameters.AddWithValue("@toName2", toEntityName);
            command.Parameters.AddWithValue("@toType2", toEntityType);
            command.Parameters.AddWithValue("@scopeKey2", scopeKey);
            command.Parameters.AddWithValue("@relType", relationshipType);
            command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            command.Parameters.AddWithValue("@weight", weight);

            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0)
                return $"Error: Could not create relationship. Verify that both endpoints exist in scope '{scopeKey}'.";

            Logger.LogInformation("Added relationship {From} -[{Type}]-> {To} in scope '{Scope}'",
                fromEntityName, relationshipType, toEntityName, scopeKey);

            return JsonSerializer.Serialize(new
            {
                fromEntity = fromEntityName,
                toEntity = toEntityName,
                relationshipType,
                scope = scopeKey,
                message = "Relationship created successfully."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AddRelationship failed: {From} -> {To} (scope '{Scope}')",
                fromEntityName, toEntityName, scopeKey);
            return $"Error adding relationship: {ex.Message}";
        }
    }

    [Description("Update an existing entity's name, description, content, or metadata. The scope parameter is required — it identifies which row to update (since the same name+type can exist in multiple scopes). Regenerates the embedding if content changes.")]
    public async Task<string> UpdateEntity(
        [Description("The current name of the entity to update")] string entityName,
        [Description("The type of the entity to update")] string entityType,
        [Description("New name (null to keep current)")] string? newName = null,
        [Description("New description (null to keep current)")] string? newDescription = null,
        [Description("New content (null to keep current)")] string? newContent = null,
        [Description("New JSON metadata (null to keep current)")] string? newMetadata = null)
    {
        var scopeKey = GetScopeKey();

        try
        {
            var setClauses = new List<string>();
            var parameters = new List<SqlParameter>
            {
                new("@entityName", entityName),
                new("@entityType", entityType),
                new("@scopeKey", scopeKey)
            };

            if (newName is not null)
            {
                setClauses.Add("Name = @newName");
                parameters.Add(new SqlParameter("@newName", newName));
            }
            if (newDescription is not null)
            {
                setClauses.Add("Description = @newDescription");
                parameters.Add(new SqlParameter("@newDescription", newDescription));
            }
            if (newContent is not null)
            {
                setClauses.Add("Content = @newContent");
                parameters.Add(new SqlParameter("@newContent", newContent));
            }
            if (newMetadata is not null)
            {
                setClauses.Add("Metadata = @newMetadata");
                parameters.Add(new SqlParameter("@newMetadata", newMetadata));
            }

            if (setClauses.Count == 0)
                return "No fields to update.";

            if (newName is not null || newDescription is not null || newContent is not null)
            {
                var embeddingText = $"{newName ?? entityName}. {newDescription ?? ""}. {newContent ?? ""}";
                try
                {
                    var embedding = await GenerateEmbeddingAsync(embeddingText);
                    setClauses.Add("Embedding = CAST(@newEmbedding AS VECTOR(1536))");
                    parameters.Add(new SqlParameter("@newEmbedding", SqlDbTypeExtensions.Vector)
                    {
                        Value = new SqlVector<float>(embedding)
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to regenerate embedding for entity '{Name}'", entityName);
                }
            }

            setClauses.Add("UpdatedAt = SYSUTCDATETIME()");

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            if (newName is not null)
            {
                var canonicalEntityId = await CanonicalEntityStore.GetOrCreateAsync(
                    connection, transaction: null, newName, entityType);
                setClauses.Add("CanonicalEntityId = @canonicalEntityId");
                parameters.Add(new SqlParameter("@canonicalEntityId", canonicalEntityId));
            }

            var schema = GraphRagSchemaInitializer.SchemaName;
            var sql = $"""
                UPDATE {schema}.KnowledgeEntity
                SET {string.Join(", ", setClauses)}
                WHERE Name = @entityName AND EntityType = @entityType AND ScopeKey = @scopeKey;
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddRange(parameters.ToArray());

            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0)
                return $"Entity '{entityName}' ({entityType}) not found in scope '{scopeKey}'.";

            Logger.LogInformation("Updated entity '{Name}' ({Type}) in scope '{Scope}'", entityName, entityType, scopeKey);
            return JsonSerializer.Serialize(new
            {
                entityName,
                entityType,
                scope = scopeKey,
                message = "Entity updated successfully."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UpdateEntity failed for '{Name}' (scope '{Scope}')", entityName, scopeKey);
            return $"Error updating entity: {ex.Message}";
        }
    }
}
