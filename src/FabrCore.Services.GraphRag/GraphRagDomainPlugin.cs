using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// Plugin for managing the knowledge hierarchy: Domains, Categories, and their
/// relationships to entities. Also handles community summary generation.
/// </summary>
[PluginAlias("graph-rag-domain")]
public class GraphRagDomainPlugin : GraphRagPluginBase
{
    protected override string PluginAlias => "graph-rag-domain";
    private IReadOnlyList<string> _allowedScopes = Array.Empty<string>();
    private string? _writeScope;

    public override Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        var scopesRaw = config.GetPluginSetting(PluginAlias, "AllowedScopes")
            ?? config.Args?.GetValueOrDefault("AllowedScopes");
        _allowedScopes = string.IsNullOrWhiteSpace(scopesRaw)
            ? Array.Empty<string>()
            : scopesRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        _writeScope = config.GetPluginSetting(PluginAlias, "WriteScope")
            ?? config.Args?.GetValueOrDefault("WriteScope")
            ?? (_allowedScopes.Count == 1 ? _allowedScopes[0] : null);

        if (_writeScope is not null && _allowedScopes.Count > 0 &&
            !_allowedScopes.Contains(_writeScope, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"WriteScope '{_writeScope}' must also be present in AllowedScopes.");
        }

        return base.InitializeAsync(config, serviceProvider);
    }

    private string GetWriteScope() => _writeScope
        ?? throw new InvalidOperationException(
            "A WriteScope is required for entity taxonomy assignments. Configure WriteScope explicitly when AllowedScopes contains multiple scopes.");

    // ─── Domain CRUD ─────────────────────────────────────────────────────

    [Description("Create a new knowledge domain (top-level organizational grouping such as 'HR', 'Equipment', 'Legal'). Domains contain categories which contain entities.")]
    public async Task<string> AddDomain(
        [Description("The name of the domain (e.g., 'HR', 'Equipment', 'Legal')")] string name,
        [Description("A short description of this domain's subject area. Pass null or empty when the description is not yet known — do not fabricate one, and never write 'auto-created from document X' style provenance.")] string? description,
        [Description("Priority weight for search boosting (higher = more relevant, default 1.0)")] double priorityWeight = 1.0,
        [Description("Optional JSON metadata string")] string? metadata = null)
    {
        try
        {
            // Plugin-level sanitizer: even if a caller (LLM tool call or code)
            // passes a provenance-shaped description, we refuse it here and write
            // SQL NULL instead. This is defense in depth on top of the ingestion
            // agent's pre-write sanitizer — if any future code path reaches the
            // plugin, the plugin itself will not let bad descriptions land.
            var cleanDescription = SanitizeDescription(description);
            if (cleanDescription is null && !string.IsNullOrWhiteSpace(description))
            {
                Logger.LogWarning(
                    "AddDomain: rejected provenance-shaped description for '{Name}'. Writing NULL instead.", name);
            }

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;
            var sql = $"""
                INSERT INTO {schema}.KnowledgeDomain (DomainId, Name, Description, PriorityWeight, Metadata)
                OUTPUT INSERTED.DomainId
                VALUES (NEWID(), @name, @description, @priorityWeight, @metadata);
                """;

            // NULL when the description was blank or rejected. A NULL column is
            // accurate ("we don't know yet") while an empty string is ambiguous.
            var descParam = cleanDescription is null ? (object)DBNull.Value : cleanDescription;

            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@description", descParam);
            cmd.Parameters.AddWithValue("@priorityWeight", priorityWeight);
            cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);

            var domainId = (Guid)(await cmd.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("INSERT did not return DomainId"));

            Logger.LogInformation("Added domain '{Name}' with ID {DomainId}, weight {Weight}", name, domainId, priorityWeight);
            return JsonSerializer.Serialize(new { domainId, name, priorityWeight, message = "Domain created successfully." }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AddDomain failed for '{Name}'", name);
            return $"Error adding domain: {ex.Message}";
        }
    }

    [Description("Create a new knowledge category within a domain (e.g., 'Safety Policies' within 'HR'). Categories group related entities together.")]
    public async Task<string> AddCategory(
        [Description("The name of the category (e.g., 'Safety Policies', 'Technical Manuals')")] string name,
        [Description("The name of the parent domain this category belongs to")] string domainName,
        [Description("A short description of this category's subject area. Pass null or empty when the description is not yet known — do not fabricate one, and never write 'auto-created from document X' style provenance.")] string? description,
        [Description("Optional JSON metadata string")] string? metadata = null)
    {
        try
        {
            // Plugin-level sanitizer — same protection as AddDomain. Provenance-
            // shaped descriptions are rejected and written as SQL NULL.
            var cleanDescription = SanitizeDescription(description);
            if (cleanDescription is null && !string.IsNullOrWhiteSpace(description))
            {
                Logger.LogWarning(
                    "AddCategory: rejected provenance-shaped description for '{Name}'. Writing NULL instead.", name);
            }

            // Generate an embedding from name + description. When the description
            // is absent (or rejected) we embed the name alone — a NULL-desc row
            // should still participate in category-level vector search, just with
            // less signal.
            var embeddingText = cleanDescription is null
                ? name
                : $"{name}. {cleanDescription}";

            float[]? embedding = null;
            try
            {
                embedding = await GenerateEmbeddingAsync(embeddingText);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to generate embedding for category '{Name}'", name);
            }

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;

            // Insert the category node
            var insertSql = $"""
                INSERT INTO {schema}.KnowledgeCategory (CategoryId, Name, Description, Embedding, Metadata)
                OUTPUT INSERTED.CategoryId
                VALUES (NEWID(), @name, @description,
                        {(embedding is not null ? "CAST(@embedding AS VECTOR(1536))" : "NULL")},
                        @metadata);
                """;

            // NULL when the description was blank or rejected — same reason as
            // AddDomain. NULL is accurate when the description is not yet authored.
            var descParam = cleanDescription is null ? (object)DBNull.Value : cleanDescription;

            Guid categoryId;
            await using (var cmd = new SqlCommand(insertSql, connection))
            {
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@description", descParam);
                cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);

                if (embedding is not null)
                {
                    cmd.Parameters.Add(new SqlParameter("@embedding", SqlDbTypeExtensions.Vector)
                    {
                        Value = new SqlVector<float>(embedding)
                    });
                }

                categoryId = (Guid)(await cmd.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("INSERT did not return CategoryId"));
            }

            // Create BelongsTo edge: Category → Domain
            var edgeSql = $"""
                INSERT INTO {schema}.BelongsTo ($from_id, $to_id)
                VALUES (
                    (SELECT $node_id FROM {schema}.KnowledgeCategory WHERE Name = @catName),
                    (SELECT $node_id FROM {schema}.KnowledgeDomain WHERE Name = @domName)
                );
                """;

            await using (var cmd = new SqlCommand(edgeSql, connection))
            {
                cmd.Parameters.AddWithValue("@catName", name);
                cmd.Parameters.AddWithValue("@domName", domainName);

                var rows = await cmd.ExecuteNonQueryAsync();
                if (rows == 0)
                    Logger.LogWarning("Failed to link category '{Category}' to domain '{Domain}' — domain may not exist", name, domainName);
            }

            Logger.LogInformation("Added category '{Name}' in domain '{Domain}' with ID {CategoryId}", name, domainName, categoryId);
            return JsonSerializer.Serialize(new { categoryId, name, domainName, message = "Category created successfully." }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AddCategory failed for '{Name}'", name);
            return $"Error adding category: {ex.Message}";
        }
    }

    // ─── Assignment ──────────────────────────────────────────────────────

    [Description("Assign an existing entity to a category. Creates a BelongsTo edge from the entity to the category.")]
    public async Task<string> AssignEntityToCategory(
        [Description("The name of the entity to assign")] string entityName,
        [Description("The type of the entity")] string entityType,
        [Description("The name of the target category")] string categoryName)
    {
        try
        {
            var scopeKey = GetWriteScope();
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;
            var sql = $"""
                INSERT INTO {schema}.BelongsTo ($from_id, $to_id, ScopeKey)
                VALUES (
                    (SELECT $node_id FROM {schema}.KnowledgeEntity WHERE Name = @entityName AND EntityType = @entityType AND ScopeKey = @scopeKey),
                    (SELECT $node_id FROM {schema}.KnowledgeCategory WHERE Name = @catName),
                    @scopeKey
                );
                """;

            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@entityName", entityName);
            cmd.Parameters.AddWithValue("@entityType", entityType);
            cmd.Parameters.AddWithValue("@catName", categoryName);
            cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0)
                return $"Error: Could not assign entity. Verify that '{entityName}' ({entityType}) and category '{categoryName}' exist.";

            Logger.LogInformation("Assigned entity '{Entity}' ({Type}) to category '{Category}'", entityName, entityType, categoryName);
            return JsonSerializer.Serialize(new { entityName, entityType, categoryName, message = "Entity assigned to category." }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AssignEntityToCategory failed: {Entity} -> {Category}", entityName, categoryName);
            return $"Error assigning entity to category: {ex.Message}";
        }
    }

    [Description("Assign an existing entity directly to a domain (for entities that don't fit a specific category).")]
    public async Task<string> AssignEntityToDomain(
        [Description("The name of the entity to assign")] string entityName,
        [Description("The type of the entity")] string entityType,
        [Description("The name of the target domain")] string domainName)
    {
        try
        {
            var scopeKey = GetWriteScope();
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;
            var sql = $"""
                INSERT INTO {schema}.BelongsTo ($from_id, $to_id, ScopeKey)
                VALUES (
                    (SELECT $node_id FROM {schema}.KnowledgeEntity WHERE Name = @entityName AND EntityType = @entityType AND ScopeKey = @scopeKey),
                    (SELECT $node_id FROM {schema}.KnowledgeDomain WHERE Name = @domName),
                    @scopeKey
                );
                """;

            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@entityName", entityName);
            cmd.Parameters.AddWithValue("@entityType", entityType);
            cmd.Parameters.AddWithValue("@domName", domainName);
            cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0)
                return $"Error: Could not assign entity. Verify that '{entityName}' ({entityType}) and domain '{domainName}' exist.";

            Logger.LogInformation("Assigned entity '{Entity}' ({Type}) to domain '{Domain}'", entityName, entityType, domainName);
            return JsonSerializer.Serialize(new { entityName, entityType, domainName, message = "Entity assigned to domain." }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AssignEntityToDomain failed: {Entity} -> {Domain}", entityName, domainName);
            return $"Error assigning entity to domain: {ex.Message}";
        }
    }

    // ─── Listing ─────────────────────────────────────────────────────────

    [Description("List all knowledge domains with their category counts.")]
    public async Task<string> ListDomains()
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;
            var sql = $"""
                SELECT d.DomainId, d.Name, d.Description, d.PriorityWeight, d.Metadata,
                       (SELECT COUNT(*)
                        FROM {schema}.KnowledgeCategory c, {schema}.BelongsTo bt
                         WHERE MATCH(c-(bt)->d) AND bt.ScopeKey IS NULL) AS CategoryCount
                FROM {schema}.KnowledgeDomain d
                ORDER BY d.Name;
                """;

            await using var cmd = new SqlCommand(sql, connection);
            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    domainId = reader.GetGuid(reader.GetOrdinal("DomainId")),
                    name = reader.GetString(reader.GetOrdinal("Name")),
                    description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                    priorityWeight = reader.GetDouble(reader.GetOrdinal("PriorityWeight")),
                    categoryCount = reader.GetInt32(reader.GetOrdinal("CategoryCount"))
                });
            }

            return results.Count == 0
                ? "No domains found."
                : JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ListDomains failed");
            return $"Error listing domains: {ex.Message}";
        }
    }

    [Description("List knowledge categories, optionally filtered by domain name, with entity counts.")]
    public async Task<string> ListCategories(
        [Description("Optional domain name to filter by")] string? domainName = null)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;
            var scopeFilter = _allowedScopes.Count == 0
                ? "AND 1 = 0"
                : $"AND e.ScopeKey IN ({string.Join(", ", Enumerable.Range(0, _allowedScopes.Count).Select(i => $"@scope{i}"))})";
            var sql = $"""
                SELECT c.CategoryId, c.Name, c.Description, c.Metadata,
                       d.Name AS DomainName,
                       (SELECT COUNT(*)
                        FROM {schema}.KnowledgeEntity e, {schema}.BelongsTo bt2
                         WHERE MATCH(e-(bt2)->c)
                           AND bt2.ScopeKey = e.ScopeKey
                           {scopeFilter}) AS EntityCount
                FROM {schema}.KnowledgeCategory c, {schema}.BelongsTo bt, {schema}.KnowledgeDomain d
                WHERE MATCH(c-(bt)->d)
                  AND bt.ScopeKey IS NULL
                {(domainName is not null ? "AND d.Name = @domainName" : "")}
                ORDER BY d.Name, c.Name;
                """;

            await using var cmd = new SqlCommand(sql, connection);
            if (domainName is not null)
                cmd.Parameters.AddWithValue("@domainName", domainName);
            for (var i = 0; i < _allowedScopes.Count; i++)
                cmd.Parameters.AddWithValue($"@scope{i}", _allowedScopes[i]);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    categoryId = reader.GetGuid(reader.GetOrdinal("CategoryId")),
                    name = reader.GetString(reader.GetOrdinal("Name")),
                    description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                    domainName = reader.GetString(reader.GetOrdinal("DomainName")),
                    entityCount = reader.GetInt32(reader.GetOrdinal("EntityCount"))
                });
            }

            return results.Count == 0
                ? "No categories found."
                : JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ListCategories failed");
            return $"Error listing categories: {ex.Message}";
        }
    }

    [Description("Get the Domain > Category hierarchy path for an entity. Returns the provenance chain.")]
    public async Task<string> GetEntityHierarchy(
        [Description("The name of the entity")] string entityName,
        [Description("The type of the entity")] string entityType)
    {
        try
        {
            var scopes = _allowedScopes.Count > 0
                ? _allowedScopes
                : _writeScope is null ? Array.Empty<string>() : [_writeScope];
            if (scopes.Count == 0)
                throw new InvalidOperationException("AllowedScopes is required to read entity taxonomy assignments.");

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;
            var scopeParams = string.Join(", ", Enumerable.Range(0, scopes.Count).Select(i => $"@scope{i}"));
            var sql = $"""
                SELECT e.CanonicalEntityId, e.ScopeKey, c.Name AS CategoryName,
                       d.Name AS DomainName, d.PriorityWeight
                FROM {schema}.KnowledgeEntity e,
                     {schema}.BelongsTo bt1, {schema}.KnowledgeCategory c,
                     {schema}.BelongsTo bt2, {schema}.KnowledgeDomain d
                WHERE MATCH(e-(bt1)->c-(bt2)->d)
                  AND e.Name = @entityName AND e.EntityType = @entityType
                  AND e.ScopeKey IN ({scopeParams})
                  AND bt1.ScopeKey = e.ScopeKey AND bt2.ScopeKey IS NULL
                UNION ALL
                SELECT e.CanonicalEntityId, e.ScopeKey, NULL AS CategoryName,
                       d.Name AS DomainName, d.PriorityWeight
                FROM {schema}.KnowledgeEntity e,
                     {schema}.BelongsTo bt, {schema}.KnowledgeDomain d
                WHERE MATCH(e-(bt)->d)
                  AND e.Name = @entityName AND e.EntityType = @entityType
                  AND e.ScopeKey IN ({scopeParams})
                  AND bt.ScopeKey = e.ScopeKey;
                """;

            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@entityName", entityName);
            cmd.Parameters.AddWithValue("@entityType", entityType);
            for (var i = 0; i < scopes.Count; i++)
                cmd.Parameters.AddWithValue($"@scope{i}", scopes[i]);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var domainName = reader.GetString(reader.GetOrdinal("DomainName"));
                var categoryName = reader.IsDBNull(reader.GetOrdinal("CategoryName"))
                    ? null : reader.GetString(reader.GetOrdinal("CategoryName"));
                results.Add(new
                {
                    canonicalEntityId = reader.GetGuid(reader.GetOrdinal("CanonicalEntityId")),
                    entityName,
                    entityType,
                    scope = reader.GetString(reader.GetOrdinal("ScopeKey")),
                    categoryName,
                    domainName,
                    priorityWeight = reader.GetDouble(reader.GetOrdinal("PriorityWeight")),
                    provenance = categoryName is null ? domainName : $"{domainName} > {categoryName}"
                });
            }

            return results.Count == 0
                ? "[]"
                : JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "GetEntityHierarchy failed for '{Entity}'", entityName);
            return $"Error getting entity hierarchy: {ex.Message}";
        }
    }

    // ─── Community Summaries ─────────────────────────────────────────────

    [Description("Generate or refresh a community summary for a category by summarizing all entities within it.")]
    public async Task<string> GenerateCommunitySummary(
        [Description("The name of the category to summarize")] string categoryName,
        [Description("The summary text to store (generated externally by the LLM agent)")] string summaryText)
    {
        try
        {
            var scopeKey = GetWriteScope();
            // Embed the summary
            float[]? embedding = null;
            try
            {
                embedding = await GenerateEmbeddingAsync(summaryText);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to generate embedding for community summary of '{Category}'", categoryName);
            }

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            var schema = GraphRagSchemaInitializer.SchemaName;

            // Get the CategoryId
            var lookupSql = $"SELECT CategoryId FROM {schema}.KnowledgeCategory WHERE Name = @name";
            Guid categoryId;
            await using (var cmd = new SqlCommand(lookupSql, connection))
            {
                cmd.Parameters.AddWithValue("@name", categoryName);
                var result = await cmd.ExecuteScalarAsync();
                if (result is null)
                    return $"Error: Category '{categoryName}' not found.";
                categoryId = (Guid)result;
            }

            // Count entities in this category
            var countSql = $"""
                SELECT COUNT(*)
                FROM {schema}.KnowledgeEntity e, {schema}.BelongsTo bt, {schema}.KnowledgeCategory c
                WHERE MATCH(e-(bt)->c) AND c.Name = @catName
                  AND e.ScopeKey = @scopeKey AND bt.ScopeKey = @scopeKey;
                """;

            int entityCount;
            await using (var cmd = new SqlCommand(countSql, connection))
            {
                cmd.Parameters.AddWithValue("@catName", categoryName);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                entityCount = (int)(await cmd.ExecuteScalarAsync())!;
            }

            // Upsert the community summary
            var upsertSql = $"""
                MERGE {schema}.CommunitySummary AS target
                USING (SELECT @categoryId AS CategoryId, @scopeKey AS ScopeKey) AS source
                ON target.CategoryId = source.CategoryId AND target.ScopeKey = source.ScopeKey
                WHEN MATCHED THEN
                    UPDATE SET Summary = @summary,
                               Embedding = {(embedding is not null ? "CAST(@embedding AS VECTOR(1536))" : "NULL")},
                               EntityCount = @entityCount,
                               UpdatedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (SummaryId, CategoryId, ScopeKey, Summary, Embedding, EntityCount)
                    VALUES (NEWID(), @categoryId, @scopeKey, @summary,
                            {(embedding is not null ? "CAST(@embedding AS VECTOR(1536))" : "NULL")},
                            @entityCount);
                """;

            await using (var cmd = new SqlCommand(upsertSql, connection))
            {
                cmd.Parameters.AddWithValue("@categoryId", categoryId);
                cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
                cmd.Parameters.AddWithValue("@summary", summaryText);
                cmd.Parameters.AddWithValue("@entityCount", entityCount);

                if (embedding is not null)
                {
                    cmd.Parameters.Add(new SqlParameter("@embedding", SqlDbTypeExtensions.Vector)
                    {
                        Value = new SqlVector<float>(embedding)
                    });
                }

                await cmd.ExecuteNonQueryAsync();
            }

            Logger.LogInformation("Generated community summary for category '{Category}' ({Entities} entities)", categoryName, entityCount);
            return JsonSerializer.Serialize(new { categoryName, categoryId, entityCount, message = "Community summary generated." }, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "GenerateCommunitySummary failed for '{Category}'", categoryName);
            return $"Error generating community summary: {ex.Message}";
        }
    }

    // ─── Internal Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Plugin-level sanitizer for domain/category descriptions. Rejects the
    /// provenance-shaped strings an LLM sometimes generates when calling
    /// <see cref="AddDomain"/> or <see cref="AddCategory"/> as a tool — e.g.
    /// "Auto-created from document: X.md", "Auto-detected from ingested
    /// documents", "from document: X", "from file: X". A rejected description
    /// yields <c>null</c>, which the callers store as SQL NULL.
    ///
    /// This is defense in depth: direct LLM tool calls to
    /// <see cref="AddDomain"/> / <see cref="AddCategory"/> could bypass
    /// caller-side sanitization. The plugin-level sanitizer is the last line
    /// of defense so provenance noise cannot land in the column regardless
    /// of caller.
    /// </summary>
    private static string? SanitizeDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.Contains("auto-created", StringComparison.OrdinalIgnoreCase)) return null;
        if (t.Contains("auto-detected", StringComparison.OrdinalIgnoreCase)) return null;
        if (t.Contains("from document:", StringComparison.OrdinalIgnoreCase)) return null;
        if (t.Contains("from file:", StringComparison.OrdinalIgnoreCase)) return null;
        if (t.StartsWith("Auto ", StringComparison.OrdinalIgnoreCase)) return null;
        return t;
    }

    /// <summary>
    /// Fetches the list of domain names from the database. Used by admin tools
    /// and legacy call sites that only need names. For classification (both
    /// ingestion-time and search-time), prefer <see cref="GetDomainsWithDescriptionsAsync"/>
    /// so the LLM can reason about subject area instead of string matching names.
    /// </summary>
    internal async Task<List<string>> GetDomainNamesAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var schema = GraphRagSchemaInitializer.SchemaName;
        var sql = $"SELECT Name FROM {schema}.KnowledgeDomain ORDER BY Name";

        await using var cmd = new SqlCommand(sql, connection);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        return names;
    }

    /// <summary>
    /// Fetches every domain with its description. Used by
    /// <c>DomainIntentClassifier</c> and the ingestion classifier to decide
    /// whether an existing domain already covers a given subject area — name
    /// alone is not enough, since two unrelated domains can collide on name
    /// (e.g. "Operations" for steel fab vs. "Operations" for IT).
    /// </summary>
    internal async Task<List<DomainSummary>> GetDomainsWithDescriptionsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var schema = GraphRagSchemaInitializer.SchemaName;
        var sql = $"SELECT Name, Description FROM {schema}.KnowledgeDomain ORDER BY Name";

        await using var cmd = new SqlCommand(sql, connection);
        var results = new List<DomainSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DomainSummary(
                Name: reader.GetString(0),
                Description: reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        return results;
    }

    /// <summary>
    /// Fetches every category (optionally filtered to a single domain) with
    /// its description and parent-domain name. Used by the ingestion classifier
    /// to choose between existing categories under the chosen domain instead
    /// of fragmenting the taxonomy on name variants.
    /// </summary>
    internal async Task<List<CategorySummary>> GetCategoriesWithDescriptionsAsync(string? domainName = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var schema = GraphRagSchemaInitializer.SchemaName;

        // MATCH join: category -[BelongsTo]-> domain. When no domainName is
        // supplied we use a LEFT-join-shaped SELECT so categories that are
        // not yet linked to a domain still appear.
        var sql = domainName is null
            ? $"""
                SELECT c.Name AS CategoryName,
                       c.Description AS CategoryDescription,
                       d.Name AS DomainName
                FROM {schema}.KnowledgeCategory c
                LEFT JOIN {schema}.BelongsTo bt ON c.$node_id = bt.$from_id
                LEFT JOIN {schema}.KnowledgeDomain d ON bt.$to_id = d.$node_id
                ORDER BY d.Name, c.Name;
                """
            : $"""
                SELECT c.Name AS CategoryName,
                       c.Description AS CategoryDescription,
                       d.Name AS DomainName
                FROM {schema}.KnowledgeCategory c, {schema}.BelongsTo bt, {schema}.KnowledgeDomain d
                WHERE MATCH(c-(bt)->d)
                  AND d.Name = @domainName
                ORDER BY c.Name;
                """;

        await using var cmd = new SqlCommand(sql, connection);
        if (domainName is not null)
            cmd.Parameters.AddWithValue("@domainName", domainName);

        var results = new List<CategorySummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new CategorySummary(
                Name: reader.GetString(reader.GetOrdinal("CategoryName")),
                Description: reader.IsDBNull(reader.GetOrdinal("CategoryDescription"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("CategoryDescription")),
                DomainName: reader.IsDBNull(reader.GetOrdinal("DomainName"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("DomainName"))));
        }
        return results;
    }

    /// <summary>
    /// Checks whether a domain with the given name exists.
    /// </summary>
    internal async Task<bool> DomainExistsAsync(string domainName)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var sql = $"SELECT COUNT(*) FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeDomain WHERE Name = @name";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", domainName);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    /// <summary>
    /// Checks whether a category with the given name exists.
    /// </summary>
    internal async Task<bool> CategoryExistsAsync(string categoryName)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var sql = $"SELECT COUNT(*) FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeCategory WHERE Name = @name";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", categoryName);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }
}

/// <summary>
/// Lightweight tuple-ish record returned by
/// <see cref="GraphRagDomainPlugin.GetDomainsWithDescriptionsAsync"/>. Used as
/// classifier context — name + description is enough signal to decide whether
/// to reuse an existing domain.
/// </summary>
internal readonly record struct DomainSummary(string Name, string? Description);

/// <summary>
/// Category counterpart to <see cref="DomainSummary"/>. Parent domain name is
/// included so ingestion-side code can filter to "categories under the
/// chosen domain" without a second round trip.
/// </summary>
internal readonly record struct CategorySummary(string Name, string? Description, string? DomainName);
