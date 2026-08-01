using System.Text;
using FabrCore.Core;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Sdk;
using Microsoft.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Default <see cref="IMemorySummaryTree"/> implementation.
///
/// <para>
/// Baseline rollup strategy: group memories by <see cref="MemoryType"/> and LLM-summarize each group
/// into a single topic node. This is intentionally simple — it buys the hierarchical-retrieval win
/// immediately. A future refinement can swap in embedding-based semantic clustering (k-means on
/// chunk embeddings, HDBSCAN, etc.) for genuinely cross-type topic nodes without changing the
/// public interface.
/// </para>
///
/// <para>
/// Persistence lives in the new <c>mem.MemorySummaryNode</c> table alongside the existing memory
/// tables — no shared state with MemoryEntity so a rebuild can truncate-and-refill safely.
/// </para>
/// </summary>
internal class MemorySummaryTreeBuilder : IMemorySummaryTree
{
    private const string SchemaName = MemorySchemaInitializer.SchemaName;

    /// <summary>Min memories in a type-group before it earns a summary node.</summary>
    private const int MinMembersForRollup = 2;

    private readonly IMemoryStore _store;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _connectionString;
    private readonly ILogger<MemorySummaryTreeBuilder> _logger;

    public MemorySummaryTreeBuilder(
        IMemoryStore store,
        AgentMemoryOptions options,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<MemorySummaryTreeBuilder>();

        _connectionString = string.IsNullOrWhiteSpace(options.ConnectionStringName)
            ? ""
            : configuration.GetConnectionString(options.ConnectionStringName) ?? "";
    }

    public async Task<int> BuildAsync(string scopeKey, CancellationToken ct = default)
    {
        if (!_options.SummaryTree.Enabled)
            return 0;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogDebug("Summary tree skipped — no connection string");
            return 0;
        }

        var headers = await _store.GetHeadersAsync(scopeKey, _options.Consolidation.MemoryFileCap, ct: ct);
        if (headers.Count < MinMembersForRollup)
            return 0;

        // Clear previous tree for this agent; rebuild is atomic-enough for the compaction window.
        await ClearAsync(scopeKey, ct);

        var groups = headers
            .GroupBy(h => h.Type)
            .Where(g => g.Count() >= MinMembersForRollup)
            .ToList();

        if (groups.Count == 0)
            return 0;

        var fanout = Math.Max(2, _options.SummaryTree.Fanout);
        var built = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            // Cap materialized members at fanout to keep the LLM input bounded.
            var sampleHeaders = group
                .OrderByDescending(h => h.UpdatedAt)
                .Take(fanout)
                .ToList();

            var lines = new List<string>();
            foreach (var h in sampleHeaders)
            {
                var chunk = await _store.GetPrimaryChunkAsync(scopeKey, h.MemoryId, ct);
                var snippet = chunk?.Content ?? h.Description ?? h.Title;
                if (snippet.Length > 400) snippet = snippet[..400] + "…";
                lines.Add($"- [{h.Title}] {snippet}");
            }

            var topic = BuildTopicLabel(group.Key);
            var summary = await LlmSummarizeAsync(topic, lines, ct);
            if (string.IsNullOrWhiteSpace(summary))
            {
                // LLM unavailable — fall back to a mechanical digest so the retrieval hit path still
                // has something to return rather than silently producing nothing.
                summary = string.Join("\n", lines);
            }

            float[]? embedding = null;
            try
            {
                embedding = await _store.GenerateEmbeddingAsync($"{topic}. {summary}", ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to embed summary node '{Topic}' for agent '{Agent}'", topic, scopeKey);
            }

            await InsertNodeAsync(new MemorySummaryNode
            {
                ScopeKey = scopeKey,
                ParentNodeId = null,
                Depth = 0,
                Topic = topic,
                Summary = summary,
                Embedding = embedding,
                MemberCount = group.Count()
            }, ct);

            built++;
        }

        _logger.LogInformation(
            "Summary tree rebuilt for agent '{Agent}': {Nodes} nodes across {Groups} type groups",
            scopeKey, built, groups.Count);

        return built;
    }

    public async Task<IReadOnlyList<MemorySummaryNode>> QueryAsync(
        string scopeKey, string query, int limit = 5, CancellationToken ct = default)
    {
        if (!_options.SummaryTree.Enabled || string.IsNullOrWhiteSpace(_connectionString))
            return [];

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _store.GenerateEmbeddingAsync(query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Summary tree query skipped — embedding failed");
            return [];
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT TOP (@limit)
                NodeId, ScopeKey, ParentNodeId, Depth, Topic, Summary, MemberCount,
                CreatedAt, UpdatedAt,
                VECTOR_DISTANCE('cosine', Embedding, CAST(@queryEmbedding AS VECTOR({_options.EmbeddingDimensions}))) AS Distance
            FROM {SchemaName}.MemorySummaryNode
            WHERE ScopeKey = @scopeKey
              AND Embedding IS NOT NULL
            ORDER BY Distance ASC;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.Add(new SqlParameter("@queryEmbedding", SqlDbTypeExtensions.Vector)
        {
            Value = new SqlVector<float>(queryEmbedding)
        });

        var results = new List<MemorySummaryNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MemorySummaryNode
            {
                NodeId = reader.GetGuid(0),
                ScopeKey = reader.GetString(1),
                ParentNodeId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                Depth = reader.GetInt32(3),
                Topic = reader.GetString(4),
                Summary = reader.GetString(5),
                MemberCount = reader.GetInt32(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<MemorySummaryNode>> GetAllAsync(
        string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return [];

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT NodeId, ScopeKey, ParentNodeId, Depth, Topic, Summary, MemberCount, CreatedAt, UpdatedAt
            FROM {SchemaName}.MemorySummaryNode
            WHERE ScopeKey = @scopeKey
            ORDER BY Depth ASC, UpdatedAt DESC;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

        var results = new List<MemorySummaryNode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MemorySummaryNode
            {
                NodeId = reader.GetGuid(0),
                ScopeKey = reader.GetString(1),
                ParentNodeId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                Depth = reader.GetInt32(3),
                Topic = reader.GetString(4),
                Summary = reader.GetString(5),
                MemberCount = reader.GetInt32(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8)
            });
        }

        return results;
    }

    public async Task ClearAsync(string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            DELETE FROM {SchemaName}.MemorySummaryNode WHERE ScopeKey = @scopeKey;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─── Private helpers ────────────────────────────────────────────────

    private async Task InsertNodeAsync(MemorySummaryNode node, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var embeddingExpr = node.Embedding is not null
            ? $"CAST(@embedding AS VECTOR({_options.EmbeddingDimensions}))"
            : "NULL";

        var sql = $"""
            INSERT INTO {SchemaName}.MemorySummaryNode
                (NodeId, ScopeKey, ParentNodeId, Depth, Topic, Summary, Embedding, MemberCount)
            VALUES (NEWID(), @scopeKey, @parentNodeId, @depth, @topic, @summary, {embeddingExpr}, @memberCount);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", node.ScopeKey);
        cmd.Parameters.AddWithValue("@parentNodeId", (object?)node.ParentNodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@depth", node.Depth);
        cmd.Parameters.AddWithValue("@topic", node.Topic);
        cmd.Parameters.AddWithValue("@summary", node.Summary);
        cmd.Parameters.AddWithValue("@memberCount", node.MemberCount);

        if (node.Embedding is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@embedding", SqlDbTypeExtensions.Vector)
            {
                Value = new SqlVector<float>(node.Embedding)
            });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string?> LlmSummarizeAsync(string topic, List<string> lines, CancellationToken ct)
    {
        var chatClientService = _serviceProvider.GetService<IFabrCoreChatClientService>();
        if (chatClientService is null) return null;

        // Summary rollups are creative writing — route via Large tier when configured, falling back
        // to the compaction model.
        var modelName = _options.Models.ResolveModelForCall(LlmModelTier.Large, _options.Models.CompactionModelName);
        IChatClient? chatClient;
        try
        {
            chatClient = await chatClientService.GetChatClient(modelName);
        }
        catch
        {
            return null;
        }
        if (chatClient is null) return null;

        var body = new StringBuilder();
        body.AppendLine($"Topic: {topic}");
        body.AppendLine("Source memories:");
        foreach (var line in lines)
            body.AppendLine(line);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, """
                You are a memory summarizer. Given a topic and a list of individual memories under it,
                produce a single coherent natural-language summary an agent can read to understand the
                topic at a glance. Preserve specifics that an agent would need to act (names, values,
                constraints). Avoid meta commentary, headings, or bulleted lists. 200 words max.
                Return ONLY the summary prose.
                """),
            new(ChatRole.User, body.ToString())
        };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
            return response.Text?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Summary LLM call failed for topic '{Topic}'", topic);
            return null;
        }
    }

    private static string BuildTopicLabel(MemoryType type) => type switch
    {
        MemoryType.Fact => "Facts: verified knowledge about the agent's domain",
        MemoryType.Rule => "Rules: constraints and policies",
        MemoryType.Instruction => "Instructions: standing user directives",
        MemoryType.Observation => "Observations: situational context and inferences",
        MemoryType.Procedural => "Procedures: reusable workflows",
        _ => $"{type} memories"
    };
}
