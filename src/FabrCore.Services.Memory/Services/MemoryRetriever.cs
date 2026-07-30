using System.Text.Json;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Three-stage memory retrieval pipeline:
/// 1. Cheap header scan (metadata only)
/// 2. LLM-based relevance selection from the manifest
/// 3. Full content retrieval for selected memories
///
/// Falls back to the manifest's recency order if the LLM call fails.
/// </summary>
internal class MemoryRetriever : IMemoryRetriever
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IMemoryStore _store;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MemoryRetriever> _logger;

    public MemoryRetriever(
        IMemoryStore store,
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<MemoryRetriever>();
    }

    public Task<IReadOnlyList<MemoryHeader>> ScanMemoryHeadersAsync(
        string scopeKey, int limit, MemoryType? typeFilter = null,
        CancellationToken ct = default)
    {
        return _store.GetHeadersAsync(scopeKey, limit, typeFilter, ct);
    }

    public async Task<IReadOnlyList<Guid>> SelectRelevantMemoriesAsync(
        string query, IReadOnlyList<MemoryHeader> manifest, int maxToSelect,
        IReadOnlySet<Guid>? excludeIds = null, CancellationToken ct = default)
    {
        if (manifest.Count == 0)
            return [];

        // Filter out already-surfaced memories
        var candidates = excludeIds is not null
            ? manifest.Where(h => !excludeIds.Contains(h.MemoryId)).ToList()
            : manifest.ToList();

        if (candidates.Count == 0)
            return [];

        // If fewer candidates than maxToSelect, return all
        if (candidates.Count <= maxToSelect)
            return candidates.Select(c => c.MemoryId).ToList();

        // Try LLM-based selection first
        try
        {
            var selected = await LlmSelectAsync(query, candidates, maxToSelect, ct);
            if (selected.Count > 0)
                return selected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM relevance selection failed, falling back to manifest recency order");
        }

        // Headers do not carry embeddings and this method intentionally has no scope key.
        // Preserve the header scan's UpdatedAt-descending order as the safe, deterministic
        // fallback instead of attempting a vector search against an invalid scope.
        return candidates
            .Take(maxToSelect)
            .Select(c => c.MemoryId)
            .ToList();
    }

    public async Task<MemoryEntry?> RetrieveMemoryAsync(
        string scopeKey, Guid memoryId, CancellationToken ct = default)
    {
        var entity = await _store.GetEntityByIdAsync(scopeKey, memoryId, ct);
        if (entity is null) return null;

        // Load primary chunk content (content lives in chunks, not on entity)
        var chunk = await _store.GetPrimaryChunkAsync(scopeKey, memoryId, ct);
        if (chunk is not null)
        {
            entity.Content = chunk.Content;
            entity.Embedding = chunk.Embedding;
        }

        return entity;
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetRelatedEntitiesAsync(
        string scopeKey, IReadOnlyList<Guid> seedEntityIds, int maxHops = 1,
        CancellationToken ct = default)
    {
        if (seedEntityIds.Count == 0 || maxHops <= 0)
            return [];

        var seedSet = new HashSet<Guid>(seedEntityIds);
        var related = new Dictionary<Guid, MemoryEntry>();

        foreach (var seedId in seedEntityIds)
        {
            try
            {
                var relationships = await _store.GetRelationshipsAsync(scopeKey, seedId, ct);
                foreach (var rel in relationships)
                {
                    if (related.ContainsKey(rel.RelatedEntityId) || seedSet.Contains(rel.RelatedEntityId))
                        continue;

                    var entity = await _store.GetEntityByIdAsync(scopeKey, rel.RelatedEntityId, ct);
                    if (entity is null) continue;

                    // Load primary chunk content
                    var chunk = await _store.GetPrimaryChunkAsync(scopeKey, rel.RelatedEntityId, ct);
                    if (chunk is not null)
                    {
                        entity.Content = chunk.Content;
                        entity.Embedding = chunk.Embedding;
                    }

                    related[rel.RelatedEntityId] = entity;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Graph traversal failed for seed entity {Id}", seedId);
            }
        }

        return related.Values.ToList();
    }

    public string? GetFreshnessWarning(MemoryHeader header)
    {
        // Point-in-time memories are always stale — they were snapshots at creation
        if (header.IsPointInTime)
        {
            var pitAge = DateTime.UtcNow - header.UpdatedAt;
            var pitAgeText = pitAge.TotalDays switch
            {
                < 1 => "earlier today",
                < 2 => "yesterday",
                _ => $"{(int)pitAge.TotalDays} days ago"
            };
            return $"[Snapshot: captured {pitAgeText}] This was a point-in-time snapshot. " +
                   "Query the source for current values.";
        }

        var age = DateTime.UtcNow - header.UpdatedAt;
        if (age.TotalDays < _options.Retrieval.FreshnessDaysThreshold)
            return null;

        var ageText = age.TotalDays switch
        {
            < 1 => "today",
            < 2 => "yesterday",
            _ => $"{(int)age.TotalDays} days ago"
        };

        return $"[Stale: last updated {ageText}] This is a point-in-time observation. " +
               "Verify against current state before relying on it.";
    }

    // ─── Private Helpers ────────────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>> LlmSelectAsync(
        string query, List<MemoryHeader> candidates, int maxToSelect, CancellationToken ct)
    {
        // Resolve IChatClient from DI (lazy, same pattern as GraphRagSearchAgent)
        var chatClientService = _serviceProvider.GetService<IFabrCoreChatClientService>();
        if (chatClientService is null)
        {
            _logger.LogDebug("No IFabrCoreChatClientService available, skipping LLM selection");
            return [];
        }

        var modelName = _options.Models.ResolveModelForCall(LlmModelTier.Small, _options.Models.RelevanceModelName);
        var chatClient = await chatClientService.GetChatClient(modelName);
        if (chatClient is null)
        {
            _logger.LogDebug("Chat client '{Model}' not available, skipping LLM selection", modelName);
            return [];
        }

        // Build manifest text (annotate point-in-time memories so LLM can deprioritize)
        var manifestText = string.Join("\n", candidates.Select(c =>
        {
            var pit = c.IsPointInTime ? " [snapshot]" : "";
            return $"[{c.Type}]{pit} {c.MemoryId:N} ({c.UpdatedAt:yyyy-MM-dd}): {c.Title} — {c.Description ?? "(no description)"}";
        }));

        var systemPrompt = """
            You are a memory retrieval agent. Your task is to select which stored memories are relevant to the current query.

            Be conservative. Only select memories that directly help answer the query or provide context the agent needs right now. An empty selection is correct when no memories are clearly relevant.

            Selection criteria:
            - Facts that ground the query in verified knowledge
            - Rules or constraints that apply to the topic at hand
            - Instructions from the user that govern how to respond
            - Observations that provide useful situational context

            Do NOT select:
            - Memories whose content is already evident in the query itself
            - Memories only tangentially related to the topic
            - Stale observations when a more recent fact covers the same ground
            - Memories marked [snapshot] unless the query specifically asks about that data and the user understands it may be outdated

            Return ONLY a JSON object with a "selected_memories" array of memory ID strings.
            """;

        var userPrompt = $"""
            Current query: {query}

            Available memories (format: [type] id (date): title — description):
            {manifestText}

            Select up to {maxToSelect} memories that are directly relevant. Return their IDs.
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        var responseText = response.Text ?? "";

        // Parse the response
        return ParseSelectedMemories(responseText, candidates);
    }

    private static IReadOnlyList<Guid> ParseSelectedMemories(string responseText, List<MemoryHeader> candidates)
    {
        try
        {
            // Extract JSON from the response (may be wrapped in markdown code blocks)
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return [];

            var json = responseText[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("selected_memories", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];

            var validIds = new HashSet<Guid>(candidates.Select(c => c.MemoryId));
            var selected = new List<Guid>();

            foreach (var item in arr.EnumerateArray())
            {
                var idStr = item.GetString();
                if (idStr is not null && Guid.TryParse(idStr, out var id) && validIds.Contains(id))
                    selected.Add(id);
            }

            return selected;
        }
        catch
        {
            return [];
        }
    }

}
