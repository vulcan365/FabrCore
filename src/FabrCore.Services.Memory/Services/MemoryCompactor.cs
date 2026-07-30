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
/// Memory consolidation engine. Performs deduplication, staleness pruning,
/// contradiction resolution, and index truncation. Inspired by the AutoDream
/// consolidation pattern: orient, gather, consolidate, prune.
/// </summary>
internal class MemoryCompactor : IMemoryCompactor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IMemoryStore _store;
    private readonly IMemoryIndexManager _indexManager;
    private readonly IMemorySummaryTree _summaryTree;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MemoryCompactor> _logger;

    public MemoryCompactor(
        IMemoryStore store,
        IMemoryIndexManager indexManager,
        IMemorySummaryTree summaryTree,
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _indexManager = indexManager;
        _summaryTree = summaryTree;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<MemoryCompactor>();
    }

    public async Task<MemoryConsolidationResult> ConsolidateAsync(string scopeKey, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting memory consolidation for agent '{Agent}'", scopeKey);

        var duplicatesMerged = await DeduplicateAsync(scopeKey, ct);
        var staleMemoriesPruned = await PruneStaleAsync(scopeKey, ct);
        var contradictionsResolved = await ResolveContradictionsAsync(scopeKey, ct);
        var evicted = await _indexManager.TruncateIndexAsync(scopeKey, ct);

        // Rebuild the hierarchical summary tree from the now-clean memory set. Runs only when
        // opt-in via AgentMemoryOptions.SummaryTreeEnabled — otherwise returns 0 immediately.
        var summaryNodesBuilt = 0;
        try
        {
            summaryNodesBuilt = await _summaryTree.BuildAsync(scopeKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Summary tree rebuild failed for agent '{Agent}' — continuing without it", scopeKey);
        }

        var result = new MemoryConsolidationResult
        {
            DuplicatesMerged = duplicatesMerged,
            StaleMemoriesPruned = staleMemoriesPruned,
            ContradictionsResolved = contradictionsResolved,
            IndexEntriesEvicted = evicted.Count,
            SummaryNodesBuilt = summaryNodesBuilt,
            ConsolidatedAt = DateTime.UtcNow
        };

        _logger.LogInformation(
            "Consolidation complete for agent '{Agent}': {Dupes} dupes merged, {Stale} stale pruned, {Contradictions} contradictions resolved, {Evicted} index entries evicted, {Summary} summary nodes",
            scopeKey, duplicatesMerged, staleMemoriesPruned, contradictionsResolved, evicted.Count, summaryNodesBuilt);

        return result;
    }

    public async Task<int> DeduplicateAsync(string scopeKey, CancellationToken ct = default)
    {
        var pairs = await _store.FindDuplicatePairsAsync(
            scopeKey, _options.Consolidation.DuplicateDistanceThreshold, ct: ct);

        if (pairs.Count == 0)
            return 0;

        var merged = 0;
        var alreadyDeleted = new HashSet<Guid>();

        foreach (var (id1, id2, distance) in pairs)
        {
            if (alreadyDeleted.Contains(id1) || alreadyDeleted.Contains(id2))
                continue;

            var entry1 = await _store.GetEntityByIdAsync(scopeKey, id1, ct);
            var entry2 = await _store.GetEntityByIdAsync(scopeKey, id2, ct);

            if (entry1 is null || entry2 is null)
                continue;

            // Load chunk content for both
            var chunk1 = await _store.GetPrimaryChunkAsync(scopeKey, id1, ct);
            var chunk2 = await _store.GetPrimaryChunkAsync(scopeKey, id2, ct);
            entry1.Content = chunk1?.Content;
            entry2.Content = chunk2?.Content;

            // Keep the newer one, merge content if the older has unique info
            var (keeper, toDelete) = entry1.UpdatedAt >= entry2.UpdatedAt
                ? (entry1, entry2)
                : (entry2, entry1);
            var keeperChunk = entry1.UpdatedAt >= entry2.UpdatedAt ? chunk1 : chunk2;

            // Try LLM-assisted merge for content
            var mergedContent = await TryMergeContentAsync(keeper, toDelete, ct);
            if (mergedContent is not null && mergedContent != keeper.Content && keeperChunk is not null)
            {
                keeperChunk.Content = mergedContent;
                try
                {
                    keeperChunk.Embedding = await _store.GenerateEmbeddingAsync(
                        $"{keeper.Title}. {keeper.Description} {mergedContent}", ct);
                }
                catch { /* Continue without new embedding */ }
                await _store.UpdateChunkAsync(scopeKey, keeperChunk, ct);
            }

            await _store.DeleteEntityAsync(scopeKey, toDelete.Id, ct);
            await _indexManager.RemoveIndexEntryAsync(scopeKey, toDelete.Id, ct);
            alreadyDeleted.Add(toDelete.Id);
            merged++;

            _logger.LogDebug("Merged duplicate memories: kept '{Keeper}' ({KeeperId}), deleted '{Deleted}' ({DeletedId}), distance={Distance:F4}",
                keeper.Title, keeper.Id, toDelete.Title, toDelete.Id, distance);
        }

        return merged;
    }

    public async Task<int> PruneStaleAsync(string scopeKey, CancellationToken ct = default)
    {
        // Get all headers
        var headers = await _store.GetHeadersAsync(scopeKey, _options.Consolidation.MemoryFileCap, ct: ct);

        // Get the hot index to know which memories are pinned
        var index = await _indexManager.GetIndexAsync(scopeKey, ct);
        var hotIds = new HashSet<Guid>(index.Entries.Select(e => e.MemoryId));

        // Find candidates: not in hot index and older than threshold
        // Point-in-time memories (snapshots) prune at 3 days; durable memories at 30 days
        var staleCandidates = headers
            .Where(h => !hotIds.Contains(h.MemoryId))
            .Where(h => h.IsPointInTime
                ? (DateTime.UtcNow - h.UpdatedAt).TotalDays > 3
                : (DateTime.UtcNow - h.UpdatedAt).TotalDays > 30)
            .ToList();

        if (staleCandidates.Count == 0)
            return 0;

        // Use LLM to confirm which are truly stale (if available)
        var confirmedStale = await ConfirmStaleWithLlmAsync(scopeKey, staleCandidates, ct);

        var pruned = 0;
        foreach (var header in confirmedStale)
        {
            // Demote to Cold instead of hard delete (archive, don't destroy)
            var entry = await _store.GetEntityByIdAsync(scopeKey, header.MemoryId, ct);
            if (entry is not null)
            {
                entry.Temperature = MemoryTemperature.Cold;
                await _store.UpdateEntityAsync(scopeKey, entry, ct);
                pruned++;
            }
        }

        return pruned;
    }

    public async Task<int> ResolveContradictionsAsync(string scopeKey, CancellationToken ct = default)
    {
        // Get recent memories (last 50) to check for contradictions
        var headers = await _store.GetHeadersAsync(scopeKey, 50, ct: ct);
        if (headers.Count < 2)
            return 0;

        var contradictionModel = _options.Models.ResolveModelForCall(LlmModelTier.Default, _options.Models.CompactionModelName);
        var chatClient = await GetChatClientAsync(contradictionModel);
        if (chatClient is null)
            return 0; // Can't resolve contradictions without LLM

        // Load recent memories with chunk content
        var recentMemories = new List<MemoryEntry>();
        foreach (var header in headers.Take(20))
        {
            var entry = await _store.GetEntityByIdAsync(scopeKey, header.MemoryId, ct);
            if (entry is null) continue;

            // Load chunk content (content lives in chunks, not on entity)
            var chunk = await _store.GetPrimaryChunkAsync(scopeKey, header.MemoryId, ct);
            if (chunk is not null)
                entry.Content = chunk.Content;

            recentMemories.Add(entry);
        }

        if (recentMemories.Count < 2)
            return 0;

        var memoryList = string.Join("\n\n", recentMemories.Select(m =>
            $"[ID: {m.Id:N}] [{m.Type}] {m.Title} (updated {m.UpdatedAt:yyyy-MM-dd}):\n{m.Description}\n{m.Content}"));

        var prompt = $"""
            Review these memories and identify contradictions. Two memories contradict when they assert incompatible claims about the same subject.

            For each contradiction found, determine which memory is more current or authoritative. Prefer:
            - Facts over Observations
            - More recent updates over older ones
            - Explicit user Instructions over inferred Observations
            - Specific Rules over general Observations

            Memories:
            {memoryList}

            Return a JSON object with a "contradictions" array. Each element:
            - "stale_id": ID of the outdated or superseded memory
            - "current_id": ID of the more authoritative memory
            - "reason": one-line explanation of the conflict

            If no contradictions exist, return an empty contradictions array.
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a memory consistency agent. Inspect the provided memories and identify pairs that assert incompatible claims about the same subject. Be precise — only flag genuine contradictions, not memories that cover different aspects of the same topic."),
            new(ChatRole.User, prompt)
        };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
            var responseText = response.Text ?? "";

            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return 0;

            var json = responseText[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("contradictions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return 0;

            var resolved = 0;
            var validIds = new HashSet<Guid>(recentMemories.Select(m => m.Id));

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("stale_id", out var staleIdProp))
                    continue;

                var staleIdStr = staleIdProp.GetString();
                if (staleIdStr is null || !Guid.TryParse(staleIdStr, out var staleId) || !validIds.Contains(staleId))
                    continue;

                // Demote stale memory to Cold (archive, don't destroy)
                var staleEntry = recentMemories.FirstOrDefault(m => m.Id == staleId);
                if (staleEntry is not null)
                {
                    staleEntry.Temperature = MemoryTemperature.Cold;
                    await _store.UpdateEntityAsync(scopeKey, staleEntry, ct);
                    await _indexManager.RemoveIndexEntryAsync(scopeKey, staleId, ct);
                    resolved++;

                    _logger.LogDebug("Resolved contradiction: demoted '{Title}' ({Id}) to Cold",
                        staleEntry.Title, staleId);
                }
            }

            return resolved;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve contradictions via LLM");
            return 0;
        }
    }

    // ─── Private Helpers ────────────────────────────────────────────────

    private async Task<string?> TryMergeContentAsync(MemoryEntry keeper, MemoryEntry toDelete, CancellationToken ct)
    {
        // If the entry to delete has no unique content, no merge needed
        if (string.IsNullOrWhiteSpace(toDelete.Content))
            return null;

        if (string.IsNullOrWhiteSpace(keeper.Content))
            return toDelete.Content;

        // Memory merge is a creative writing task — prefer the Large tier when configured.
        var mergeModel = _options.Models.ResolveModelForCall(LlmModelTier.Large, _options.Models.CompactionModelName);
        var chatClient = await GetChatClientAsync(mergeModel);
        if (chatClient is null)
            return null; // Can't merge without LLM, just keep the newer

        try
        {
            var prompt = $"""
                These two memories are near-duplicates. Merge them into one.

                Use the newer entry as the base. Incorporate any specific details from the older entry that the newer one does not already capture. Drop redundant or restated content.

                Newer ({keeper.Title}, {keeper.UpdatedAt:yyyy-MM-dd}):
                {keeper.Content}

                Older ({toDelete.Title}, {toDelete.UpdatedAt:yyyy-MM-dd}):
                {toDelete.Content}

                Return ONLY the merged content text.
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are a memory merge agent. Combine two near-duplicate memories into one, preserving all unique details. Return only the merged content — no commentary, no wrapper."),
                new(ChatRole.User, prompt)
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
            return response.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM merge failed, keeping newer content only");
            return null;
        }
    }

    private async Task<List<MemoryHeader>> ConfirmStaleWithLlmAsync(
        string scopeKey, List<MemoryHeader> candidates, CancellationToken ct)
    {
        // Staleness confirmation is a classification task — route via Small tier.
        var staleModel = _options.Models.ResolveModelForCall(LlmModelTier.Small, _options.Models.CompactionModelName);
        var chatClient = await GetChatClientAsync(staleModel);
        if (chatClient is null)
            return candidates; // Without LLM confirmation, assume all candidates are stale

        try
        {
            var candidateList = string.Join("\n", candidates.Select(c =>
                $"- {c.MemoryId:N}: [{c.Type}] {c.Title} (updated {c.UpdatedAt:yyyy-MM-dd}) — {c.Description ?? "(no description)"}"));

            var prompt = $"""
                Review these memory entries. Each has not been updated in over 30 days and is not in the active memory index.

                Determine which are genuinely stale — meaning they are likely outdated, superseded by newer knowledge, or no longer applicable.

                Be conservative. Archive only when you have clear reason to believe the memory is no longer accurate or useful:
                - Facts that describe states which have likely changed
                - Observations that were situational and time-bound
                - Rules tied to conditions that are probably no longer active

                Do NOT archive:
                - Instructions from the user (these persist until explicitly revoked)
                - Stable facts about systems, processes, or domain knowledge
                - Rules that are likely still in effect

                Candidates:
                {candidateList}

                Return a JSON object with a "stale_ids" array of IDs to archive. Empty array if none are stale.
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are a memory staleness evaluator. Inspect each candidate and determine whether it is genuinely outdated. When uncertain, keep the memory — false negatives are acceptable, false positives lose knowledge."),
                new(ChatRole.User, prompt)
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
            var responseText = response.Text ?? "";

            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return [];

            var json = responseText[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("stale_ids", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];

            var staleIds = new HashSet<Guid>();
            foreach (var item in arr.EnumerateArray())
            {
                var idStr = item.GetString();
                if (idStr is not null && Guid.TryParse(idStr, out var id))
                    staleIds.Add(id);
            }

            return candidates.Where(c => staleIds.Contains(c.MemoryId)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM staleness confirmation failed, returning empty (conservative)");
            return [];
        }
    }

    private async Task<IChatClient?> GetChatClientAsync(string modelName)
    {
        try
        {
            var chatClientService = _serviceProvider.GetService<IFabrCoreChatClientService>();
            if (chatClientService is null)
                return null;

            return await chatClientService.GetChatClient(modelName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve chat client '{Model}'", modelName);
            return null;
        }
    }
}
