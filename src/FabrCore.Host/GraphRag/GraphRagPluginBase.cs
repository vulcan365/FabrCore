using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag;

/// <summary>
/// Shared base for GraphRAG plugins. Handles connection string resolution,
/// embedding generation, and common SQL helpers.
/// </summary>
public abstract class GraphRagPluginBase : IFabrCorePlugin, IAsyncDisposable
{
    protected string ConnectionString = "";
    protected ILogger Logger = default!;
    protected IEmbeddings? Embeddings;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    protected abstract string PluginAlias { get; }

    public virtual Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        Logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());

        var connStringName = config.GetPluginSetting(PluginAlias, "ConnectionStringName")
            ?? config.Args?.GetValueOrDefault("ConnectionStringName")
            ?? throw new InvalidOperationException(
                $"{PluginAlias}:ConnectionStringName or ConnectionStringName arg is required");

        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        ConnectionString = configuration.GetConnectionString(connStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connStringName}' not found in configuration");

        // IEmbeddings is auto-registered by AddFabrCoreServer() using the "embeddings"
        // model entry in fabrcore.json. Available via DI in the FabrCore host.
        Embeddings = serviceProvider.GetService<IEmbeddings>();

        Logger.LogInformation("{Plugin} initialized with connection '{Name}', embeddings: {HasEmbeddings}",
            PluginAlias, connStringName, Embeddings is not null);
        return Task.CompletedTask;
    }

    protected async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (Embeddings is null)
            throw new InvalidOperationException(
                "No IEmbeddings registered. Ensure AddFabrCoreServer() is configured " +
                "with an 'embeddings' model entry in fabrcore.json.");

        var result = await Embeddings.GetEmbeddings(text);
        return result.Vector.ToArray();
    }

    protected static string BuildMatchQuery(string schema, int depth, string? relationshipTypeFilter)
    {
        var sql = new StringBuilder();

        if (depth == 1)
        {
            sql.AppendLine($"""
                SELECT
                    e1.Name AS SourceName, e1.EntityType AS SourceType,
                    r.RelationshipType, r.Description AS RelationshipDescription, r.Weight,
                    e2.Name AS TargetName, e2.EntityType AS TargetType, e2.Description AS TargetDescription
                FROM {schema}.KnowledgeEntity e1, {schema}.KnowledgeRelationship r, {schema}.KnowledgeEntity e2
                WHERE MATCH(e1-(r)->e2)
                    AND e1.Name = @entityName
                """);

            if (!string.IsNullOrEmpty(relationshipTypeFilter))
                sql.AppendLine("    AND r.RelationshipType = @relType");
        }
        else if (depth == 2)
        {
            sql.AppendLine($"""
                SELECT
                    e1.Name AS SourceName, e1.EntityType AS SourceType,
                    r1.RelationshipType AS Rel1Type, r1.Weight AS Rel1Weight,
                    e2.Name AS Hop1Name, e2.EntityType AS Hop1Type,
                    r2.RelationshipType AS Rel2Type, r2.Weight AS Rel2Weight,
                    e3.Name AS Hop2Name, e3.EntityType AS Hop2Type, e3.Description AS Hop2Description
                FROM {schema}.KnowledgeEntity e1,
                     {schema}.KnowledgeRelationship r1, {schema}.KnowledgeEntity e2,
                     {schema}.KnowledgeRelationship r2, {schema}.KnowledgeEntity e3
                WHERE MATCH(e1-(r1)->e2-(r2)->e3)
                    AND e1.Name = @entityName
                """);

            if (!string.IsNullOrEmpty(relationshipTypeFilter))
                sql.AppendLine("    AND (r1.RelationshipType = @relType OR r2.RelationshipType = @relType)");
        }
        else // depth == 3
        {
            sql.AppendLine($"""
                SELECT
                    e1.Name AS SourceName, e1.EntityType AS SourceType,
                    r1.RelationshipType AS Rel1Type,
                    e2.Name AS Hop1Name, e2.EntityType AS Hop1Type,
                    r2.RelationshipType AS Rel2Type,
                    e3.Name AS Hop2Name, e3.EntityType AS Hop2Type,
                    r3.RelationshipType AS Rel3Type,
                    e4.Name AS Hop3Name, e4.EntityType AS Hop3Type, e4.Description AS Hop3Description
                FROM {schema}.KnowledgeEntity e1,
                     {schema}.KnowledgeRelationship r1, {schema}.KnowledgeEntity e2,
                     {schema}.KnowledgeRelationship r2, {schema}.KnowledgeEntity e3,
                     {schema}.KnowledgeRelationship r3, {schema}.KnowledgeEntity e4
                WHERE MATCH(e1-(r1)->e2-(r2)->e3-(r3)->e4)
                    AND e1.Name = @entityName
                """);

            if (!string.IsNullOrEmpty(relationshipTypeFilter))
                sql.AppendLine("    AND (r1.RelationshipType = @relType OR r2.RelationshipType = @relType OR r3.RelationshipType = @relType)");
        }

        return sql.ToString();
    }

    /// <summary>
    /// Builds a domain-aware MATCH query that includes hierarchy LEFT JOINs and
    /// a ContextScore computed from domain PriorityWeight and relationship Weight.
    /// Uses a CTE to separate the MATCH traversal from the hierarchy JOINs, because
    /// SQL Server does not allow LEFT JOIN on node aliases used in MATCH clauses.
    /// </summary>
    protected static string BuildDomainAwareMatchQuery(string schema, int depth, string? relationshipTypeFilter, string? domainFilter)
    {
        var sql = new StringBuilder();
        var hasDomainFilter = !string.IsNullOrEmpty(domainFilter);

        // CTE: perform the MATCH traversal first (no JOINs on MATCH aliases)
        // Then outer query JOINs hierarchy for domain/category provenance.

        if (depth == 1)
        {
            sql.AppendLine($"""
                ;WITH GraphResults AS (
                    SELECT
                        e1.Name AS SourceName, e1.EntityType AS SourceType,
                        r.RelationshipType, r.Description AS RelationshipDescription, r.Weight,
                        e2.Name AS TargetName, e2.EntityType AS TargetType,
                        e2.Description AS TargetDescription, e2.EntityId AS TargetEntityId
                    FROM {schema}.KnowledgeEntity e1, {schema}.KnowledgeRelationship r, {schema}.KnowledgeEntity e2
                    WHERE MATCH(e1-(r)->e2)
                        AND e1.Name = @entityName
                """);

            if (!string.IsNullOrEmpty(relationshipTypeFilter))
                sql.AppendLine("        AND r.RelationshipType = @relType");

            sql.AppendLine($"""
                )
                SELECT gr.*,
                    d.Name AS DomainName, d.PriorityWeight,
                    cat.Name AS CategoryName,
                    ISNULL(d.PriorityWeight, 1.0) * gr.Weight AS ContextScore
                FROM GraphResults gr
                LEFT JOIN {schema}.KnowledgeEntity te ON gr.TargetEntityId = te.EntityId
                LEFT JOIN {schema}.BelongsTo bt_ec ON te.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = te.ScopeKey
                LEFT JOIN {schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
                LEFT JOIN {schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
                LEFT JOIN {schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
                """);

            if (hasDomainFilter)
                sql.AppendLine("WHERE (d.Name = @domainFilter OR d.Name IS NULL)");

            sql.AppendLine("ORDER BY ContextScore DESC");
        }
        else if (depth == 2)
        {
            sql.AppendLine($"""
                ;WITH GraphResults AS (
                    SELECT
                        e1.Name AS SourceName, e1.EntityType AS SourceType,
                        r1.RelationshipType AS Rel1Type, r1.Weight AS Rel1Weight,
                        e2.Name AS Hop1Name, e2.EntityType AS Hop1Type,
                        r2.RelationshipType AS Rel2Type, r2.Weight AS Rel2Weight,
                        e3.Name AS Hop2Name, e3.EntityType AS Hop2Type,
                        e3.Description AS Hop2Description, e3.EntityId AS TerminalEntityId
                    FROM {schema}.KnowledgeEntity e1,
                         {schema}.KnowledgeRelationship r1, {schema}.KnowledgeEntity e2,
                         {schema}.KnowledgeRelationship r2, {schema}.KnowledgeEntity e3
                    WHERE MATCH(e1-(r1)->e2-(r2)->e3)
                        AND e1.Name = @entityName
                """);

            if (!string.IsNullOrEmpty(relationshipTypeFilter))
                sql.AppendLine("        AND (r1.RelationshipType = @relType OR r2.RelationshipType = @relType)");

            sql.AppendLine($"""
                )
                SELECT gr.*,
                    d.Name AS DomainName, d.PriorityWeight,
                    cat.Name AS CategoryName,
                    ISNULL(d.PriorityWeight, 1.0) * gr.Rel1Weight * gr.Rel2Weight AS ContextScore
                FROM GraphResults gr
                LEFT JOIN {schema}.KnowledgeEntity te ON gr.TerminalEntityId = te.EntityId
                LEFT JOIN {schema}.BelongsTo bt_ec ON te.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = te.ScopeKey
                LEFT JOIN {schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
                LEFT JOIN {schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
                LEFT JOIN {schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
                """);

            if (hasDomainFilter)
                sql.AppendLine("WHERE (d.Name = @domainFilter OR d.Name IS NULL)");

            sql.AppendLine("ORDER BY ContextScore DESC");
        }
        else // depth == 3
        {
            sql.AppendLine($"""
                ;WITH GraphResults AS (
                    SELECT
                        e1.Name AS SourceName, e1.EntityType AS SourceType,
                        r1.RelationshipType AS Rel1Type, r1.Weight AS Rel1Weight,
                        e2.Name AS Hop1Name, e2.EntityType AS Hop1Type,
                        r2.RelationshipType AS Rel2Type, r2.Weight AS Rel2Weight,
                        e3.Name AS Hop2Name, e3.EntityType AS Hop2Type,
                        r3.RelationshipType AS Rel3Type, r3.Weight AS Rel3Weight,
                        e4.Name AS Hop3Name, e4.EntityType AS Hop3Type,
                        e4.Description AS Hop3Description, e4.EntityId AS TerminalEntityId
                    FROM {schema}.KnowledgeEntity e1,
                         {schema}.KnowledgeRelationship r1, {schema}.KnowledgeEntity e2,
                         {schema}.KnowledgeRelationship r2, {schema}.KnowledgeEntity e3,
                         {schema}.KnowledgeRelationship r3, {schema}.KnowledgeEntity e4
                    WHERE MATCH(e1-(r1)->e2-(r2)->e3-(r3)->e4)
                        AND e1.Name = @entityName
                """);

            if (!string.IsNullOrEmpty(relationshipTypeFilter))
                sql.AppendLine("        AND (r1.RelationshipType = @relType OR r2.RelationshipType = @relType OR r3.RelationshipType = @relType)");

            sql.AppendLine($"""
                )
                SELECT gr.*,
                    d.Name AS DomainName, d.PriorityWeight,
                    cat.Name AS CategoryName,
                    ISNULL(d.PriorityWeight, 1.0) * gr.Rel1Weight * gr.Rel2Weight * gr.Rel3Weight AS ContextScore
                FROM GraphResults gr
                LEFT JOIN {schema}.KnowledgeEntity te ON gr.TerminalEntityId = te.EntityId
                LEFT JOIN {schema}.BelongsTo bt_ec ON te.$node_id = bt_ec.$from_id AND bt_ec.ScopeKey = te.ScopeKey
                LEFT JOIN {schema}.KnowledgeCategory cat ON bt_ec.$to_id = cat.$node_id
                LEFT JOIN {schema}.BelongsTo bt_cd ON cat.$node_id = bt_cd.$from_id AND bt_cd.ScopeKey IS NULL
                LEFT JOIN {schema}.KnowledgeDomain d ON bt_cd.$to_id = d.$node_id
                """);

            if (hasDomainFilter)
                sql.AppendLine("WHERE (d.Name = @domainFilter OR d.Name IS NULL)");

            sql.AppendLine("ORDER BY ContextScore DESC");
        }

        return sql.ToString();
    }

    /// <summary>
    /// Splits content into chunks with optional overlap. Uses paragraph boundaries
    /// as the primary split point, falling back to sentence boundaries for oversized
    /// paragraphs. Overlap creates a sliding window so context at chunk boundaries
    /// is preserved for better semantic search recall.
    /// </summary>
    /// <param name="content">The text to chunk.</param>
    /// <param name="chunkSize">Target maximum characters per chunk (default 500).</param>
    /// <param name="overlapChars">
    /// Number of characters from the end of the previous chunk to repeat at the start
    /// of the next chunk (default 100). Set to 0 for no overlap. Clamped to half of
    /// chunkSize to prevent degenerate cases.
    /// </param>
    internal static List<string> SplitIntoChunks(string content, int chunkSize, int overlapChars = 100)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        // Clamp overlap to at most half the chunk size
        overlapChars = Math.Clamp(overlapChars, 0, chunkSize / 2);

        // Phase 1: Build raw (non-overlapping) segments from paragraphs/sentences
        var segments = new List<string>();
        var paragraphs = content.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);

        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim();
            if (trimmed.Length == 0) continue;

            if (current.Length > 0 && current.Length + trimmed.Length + 2 > chunkSize)
            {
                segments.Add(current.ToString().Trim());
                current.Clear();
            }

            if (trimmed.Length > chunkSize)
            {
                if (current.Length > 0)
                {
                    segments.Add(current.ToString().Trim());
                    current.Clear();
                }

                var sentences = trimmed.Split([". ", "! ", "? "], StringSplitOptions.RemoveEmptyEntries);
                foreach (var sentence in sentences)
                {
                    if (current.Length > 0 && current.Length + sentence.Length + 2 > chunkSize)
                    {
                        segments.Add(current.ToString().Trim());
                        current.Clear();
                    }
                    if (current.Length > 0) current.Append(' ');
                    current.Append(sentence.TrimEnd('.', '!', '?'));
                    current.Append('.');
                }
            }
            else
            {
                if (current.Length > 0) current.Append("\n\n");
                current.Append(trimmed);
            }
        }

        if (current.Length > 0)
            segments.Add(current.ToString().Trim());

        // Phase 2: If no overlap requested, return raw segments as-is
        if (overlapChars == 0 || segments.Count <= 1)
            return segments;

        // Phase 3: Apply overlap — prepend tail of previous segment to each subsequent segment
        var chunks = new List<string>(segments.Count) { segments[0] };

        for (var i = 1; i < segments.Count; i++)
        {
            var previous = segments[i - 1];
            var overlapText = previous.Length <= overlapChars
                ? previous
                : previous[^overlapChars..];

            // Try to start the overlap at a word boundary to avoid mid-word cuts
            var wordBoundary = overlapText.IndexOf(' ');
            if (wordBoundary > 0)
                overlapText = overlapText[wordBoundary..].TrimStart();

            chunks.Add($"{overlapText}\n\n{segments[i]}");
        }

        return chunks;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
