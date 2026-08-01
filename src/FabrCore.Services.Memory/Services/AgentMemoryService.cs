using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FabrCore.Core;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Main memory service facade. Orchestrates the knowledge graph (entity nodes,
/// content chunks, relationship edges), taxonomy validation, entity matching,
/// retrieval pipeline, and compaction. Scoped to a single memory scope — an agent
/// handle (isolated) or a named shared scope used by multiple agents.
/// </summary>
internal partial class AgentMemoryService : IAgentMemoryService
{
    /// <summary>Marker tokens that wrap recalled memory content in the conversation.</summary>
    internal const string MemoryContextStart = "<memory-context source=\"agent-memory-system\">";
    internal const string MemoryContextEnd = "</memory-context>";

    private readonly IMemoryStore _store;
    private readonly IMemoryIndexManager _indexManager;
    private readonly IMemoryRetriever _retriever;
    private readonly IMemoryCompactor _compactor;
    private readonly IRetrievalPlanner _planner;
    private readonly IMemorySummaryTree _summaryTree;
    private readonly IMemoryScopeService _scopeService;
    private readonly IMemoryAuditLog _auditLog;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentMemoryService> _logger;

    private volatile bool _scopeEnsured;
    private int _consolidationRunning;

    public string ScopeKey { get; }

    public AgentMemoryService(
        string scopeKey,
        IMemoryStore store,
        IMemoryIndexManager indexManager,
        IMemoryRetriever retriever,
        IMemoryCompactor compactor,
        IRetrievalPlanner planner,
        IMemorySummaryTree summaryTree,
        IMemoryScopeService scopeService,
        IMemoryAuditLog auditLog,
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        ScopeKey = scopeKey;
        _store = store;
        _indexManager = indexManager;
        _retriever = retriever;
        _compactor = compactor;
        _planner = planner;
        _summaryTree = summaryTree;
        _scopeService = scopeService;
        _auditLog = auditLog;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<AgentMemoryService>();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Save — with Entity Matching
    // ═══════════════════════════════════════════════════════════════════

    public async Task<MemoryEntry> SaveMemoryAsync(
        string title, MemoryType type, string content,
        string? description = null, Dictionary<string, string>? metadata = null,
        bool isPointInTime = false,
        CancellationToken ct = default)
    {
        // 1. Validate taxonomy
        var (isValid, reason) = MemoryTaxonomyRules.Validate(type, content, _options.AllowedMemoryTypes);
        if (!isValid)
            throw new InvalidOperationException($"Memory taxonomy validation failed: {reason}");

        // 2. Generate embedding for incoming content
        var embeddingText = BuildEmbeddingText(title, description, content);
        float[]? embedding = null;
        try
        {
            embedding = await _store.GenerateEmbeddingAsync(embeddingText, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate embedding for memory '{Title}', saving without entity matching", title);
        }

        // 3. Entity matching — search existing chunks for similar content
        if (embedding is not null)
        {
            try
            {
                var matches = await _store.FindSimilarByContentAsync(
                    ScopeKey, embedding, limit: 3,
                    maxDistance: _options.Consolidation.EntityMatchThreshold, ct);

                // Filter to same type (Fact matches Fact, not Rule)
                var bestMatch = matches.FirstOrDefault(m => m.Entity.Type == type);

                if (bestMatch != default)
                {
                    // UPDATE existing entity — merge knowledge
                    return await MergeIntoExistingEntityAsync(
                        bestMatch.Entity, bestMatch.Chunk,
                        title, content, description, isPointInTime, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Entity matching failed for '{Title}', falling back to new entity creation", title);
            }
        }

        // 4. No match — create new entity + chunk
        var entry = new MemoryEntry
        {
            Title = title,
            Type = type,
            Temperature = MemoryTemperature.Warm,
            Description = description ?? title,
            IsPointInTime = isPointInTime,
            Metadata = metadata
        };

        entry = await _store.InsertEntityAsync(ScopeKey, entry, ct);

        var chunk = new MemoryChunkEntry
        {
            EntityId = entry.Id,
            Content = content,
            Embedding = embedding,
            ChunkIndex = 0
        };
        chunk = await _store.InsertChunkAsync(ScopeKey, chunk, ct);

        // Populate the content on the entry for callers
        entry.Content = content;
        entry.Embedding = embedding;

        // 5. Add to hot index
        await AddToHotIndexAsync(entry, ct);

        // 6. First successful write registers the scope so admin tooling can enumerate it
        await EnsureScopeRegisteredAsync(ct);

        // 7. Auto-consolidation if enabled and over cap
        await TryAutoConsolidateAsync(ct);

        _logger.LogInformation("Saved new memory '{Title}' ({Type}) in scope '{Scope}' with ID {Id}",
            title, type, ScopeKey, entry.Id);

        await _auditLog.RecordAsync("MemorySaved", ScopeKey, entry.Id, summary: title, actorId: ScopeKey, ct: ct);

        return entry;
    }

    private async Task<MemoryEntry> MergeIntoExistingEntityAsync(
        MemoryEntry existingEntity, MemoryChunkEntry existingChunk,
        string newTitle, string newContent, string? newDescription,
        bool isPointInTime, CancellationToken ct)
    {
        // LLM merge: combine old and new knowledge
        var mergedContent = await TryMergeContentAsync(existingChunk.Content, newContent, ct)
            ?? newContent; // Fallback: just use the newer content

        // Regenerate embedding for merged content
        var embeddingText = BuildEmbeddingText(
            existingEntity.Title, newDescription ?? existingEntity.Description, mergedContent);
        float[]? newEmbedding = null;
        try
        {
            newEmbedding = await _store.GenerateEmbeddingAsync(embeddingText, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to regenerate embedding during entity merge for '{Title}'", existingEntity.Title);
        }

        // Update chunk content + embedding
        existingChunk.Content = mergedContent;
        if (newEmbedding is not null)
            existingChunk.Embedding = newEmbedding;
        await _store.UpdateChunkAsync(ScopeKey, existingChunk, ct);

        // Update entity metadata
        if (newDescription is not null)
            existingEntity.Description = newDescription;
        existingEntity.IsPointInTime = isPointInTime;
        existingEntity = await _store.UpdateEntityAsync(ScopeKey, existingEntity, ct);

        // Update hot index entry
        await AddToHotIndexAsync(existingEntity, ct);

        existingEntity.Content = mergedContent;
        existingEntity.Embedding = newEmbedding;

        _logger.LogInformation("Merged into existing memory '{Title}' ({Id}) in scope '{Scope}'",
            existingEntity.Title, existingEntity.Id, ScopeKey);

        await _auditLog.RecordAsync("MemoryMerged", ScopeKey, existingEntity.Id,
            summary: existingEntity.Title, actorId: ScopeKey, ct: ct);

        return existingEntity;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Recall — Parallel 3-Path Retrieval
    // ═══════════════════════════════════════════════════════════════════

    public async Task<MemoryRecallResult> RecallAsync(
        string query,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default)
    {
        var result = new MemoryRecallResult();

        // Step 1: Always fetch the hot index — minimal cost, needed for the planner.
        result.HotIndex = await _indexManager.GetIndexAsync(ScopeKey, ct);

        // Step 2: Ask the planner for a plan.
        var plan = await _planner.CreatePlanAsync(query, result.HotIndex, ct);
        result.Plan = plan;

        // Step 3: If the plan is hot-index-only, we are done. Zero LLM / vector cost beyond the planner itself.
        if (plan.Steps.Count == 0 ||
            (plan.Steps.Count == 1 && plan.Steps[0] == RetrievalStep.HotIndexOnly))
        {
            _logger.LogDebug(
                "Recall (plan=HotIndexOnly, src={Source}, reason={Rationale}) for agent '{Agent}': {HotCount} hot",
                plan.Source, plan.Rationale, ScopeKey, result.HotIndex.Entries.Count);
            return result;
        }

        // Step 4: Execute the plan step-by-step. Headers and selection are computed lazily and cached
        // so repeated steps do not double-charge.
        IReadOnlyList<MemoryHeader>? headers = null;
        var seenIds = new HashSet<Guid>();

        foreach (var step in plan.Steps)
        {
            switch (step)
            {
                case RetrievalStep.HotIndexOnly:
                    break;

                case RetrievalStep.HeaderScanLlmSelect:
                {
                    headers ??= await _retriever.ScanMemoryHeadersAsync(
                        ScopeKey, _options.Retrieval.HeaderScanLimit, ct: ct);

                    var candidates = FilterByPreferredTypes(headers, plan.PreferredTypes);
                    var selectedIds = await _retriever.SelectRelevantMemoriesAsync(
                        query, candidates, _options.Retrieval.WarmRetrievalLimit, alreadySurfacedIds, ct);

                    var loadTask = LoadMemoriesWithChunksAsync(selectedIds, ct);
                    var graphTask = plan.Steps.Contains(RetrievalStep.GraphExpand) && _options.Retrieval.RecallGraphHops > 0
                        ? _retriever.GetRelatedEntitiesAsync(ScopeKey, selectedIds, _options.Retrieval.RecallGraphHops, ct)
                        : Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

                    await Task.WhenAll(loadTask, graphTask);

                    foreach (var mem in loadTask.Result)
                        if (seenIds.Add(mem.Id)) result.WarmMemories.Add(mem);

                    foreach (var related in graphTask.Result)
                        if (seenIds.Add(related.Id)) result.WarmMemories.Add(related);

                    break;
                }

                case RetrievalStep.VectorOnly:
                {
                    var queryEmbedding = await _store.GenerateEmbeddingAsync(query, ct);
                    var vectorMatches = await _store.VectorSearchAsync(
                        ScopeKey, queryEmbedding, _options.Retrieval.WarmRetrievalLimit, ct: ct);

                    foreach (var match in vectorMatches)
                    {
                        if (alreadySurfacedIds is not null && alreadySurfacedIds.Contains(match.Entry.Id))
                            continue;
                        if (plan.PreferredTypes is not null && !plan.PreferredTypes.Contains(match.Entry.Type))
                            continue;
                        if (seenIds.Add(match.Entry.Id))
                            result.WarmMemories.Add(match.Entry);
                    }

                    break;
                }

                case RetrievalStep.GraphExpand:
                    // Handled inline by HeaderScanLlmSelect when present. If this step runs standalone
                    // (unusual), there is nothing to seed from, so it is a no-op.
                    break;

                case RetrievalStep.ArchiveSearch:
                {
                    try
                    {
                        var archiveResults = await SearchArchiveAsync(
                            query, limit: _options.Retrieval.WarmRetrievalLimit, ct: ct);

                        foreach (var r in archiveResults)
                        {
                            if (alreadySurfacedIds is not null && alreadySurfacedIds.Contains(r.Entry.Id))
                                continue;
                            // Skip duplicates already in warm set — they are the same memory.
                            if (seenIds.Contains(r.Entry.Id))
                                continue;
                            result.ArchiveResults.Add(r);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Archive search step failed for agent '{Agent}'", ScopeKey);
                    }
                    break;
                }

                case RetrievalStep.SummaryTreeScan:
                {
                    try
                    {
                        var summaryNodes = await _summaryTree.QueryAsync(
                            ScopeKey, query, limit: _options.Retrieval.WarmRetrievalLimit, ct);
                        result.SummaryNodes.AddRange(summaryNodes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Summary tree scan step failed for agent '{Agent}'", ScopeKey);
                    }
                    break;
                }
            }
        }

        // Step 5: Freshness warnings for warm memories (headers are the source of truth for UpdatedAt).
        if (result.WarmMemories.Count > 0)
        {
            var headerLookup = (headers ?? [])
                .GroupBy(h => h.MemoryId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var warm in result.WarmMemories)
            {
                if (headerLookup.TryGetValue(warm.Id, out var header))
                {
                    var warning = _retriever.GetFreshnessWarning(header);
                    if (warning is not null)
                        result.FreshnessWarnings.Add($"{header.Title}: {warning}");
                }
                else if (warm.IsPointInTime)
                {
                    var pitHeader = new MemoryHeader
                    {
                        MemoryId = warm.Id, Title = warm.Title, Type = warm.Type,
                        UpdatedAt = warm.UpdatedAt, IsPointInTime = true
                    };
                    var warning = _retriever.GetFreshnessWarning(pitHeader);
                    if (warning is not null)
                        result.FreshnessWarnings.Add($"{warm.Title}: {warning}");
                }
            }
        }

        _logger.LogDebug(
            "Recall (plan=[{Steps}], src={Source}) for agent '{Agent}': {HotCount} hot, {WarmCount} warm, {ArchiveCount} archive, {Warnings} warnings",
            string.Join(",", plan.Steps), plan.Source, ScopeKey,
            result.HotIndex.Entries.Count, result.WarmMemories.Count, result.ArchiveResults.Count, result.FreshnessWarnings.Count);

        return result;
    }

    private static IReadOnlyList<MemoryHeader> FilterByPreferredTypes(
        IReadOnlyList<MemoryHeader> headers, HashSet<MemoryType>? preferredTypes)
    {
        if (preferredTypes is null || preferredTypes.Count == 0)
            return headers;

        // Soft bias: move preferred types to the front, keep the rest as fallback candidates.
        // The LLM still gets the full list but anchored on the preferred types.
        var preferred = headers.Where(h => preferredTypes.Contains(h.Type)).ToList();
        var rest = headers.Where(h => !preferredTypes.Contains(h.Type)).ToList();
        preferred.AddRange(rest);
        return preferred;
    }

    private async Task<List<MemoryEntry>> LoadMemoriesWithChunksAsync(
        IReadOnlyList<Guid> entityIds, CancellationToken ct)
    {
        var entries = new List<MemoryEntry>();
        foreach (var id in entityIds)
        {
            var entry = await _store.GetEntityByIdAsync(ScopeKey, id, ct);
            if (entry is null) continue;

            // Load primary chunk content
            var chunk = await _store.GetPrimaryChunkAsync(ScopeKey, id, ct);
            if (chunk is not null)
            {
                entry.Content = chunk.Content;
                entry.Embedding = chunk.Embedding;
            }

            // Load relationships if graph recall is enabled
            if (_options.Retrieval.RecallGraphHops > 0)
            {
                var relationships = await _store.GetRelationshipsAsync(ScopeKey, id, ct);
                if (relationships.Count > 0)
                    entry.Relationships = relationships.ToList();
            }

            entries.Add(entry);
        }
        return entries;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Format Recall Context
    // ═══════════════════════════════════════════════════════════════════

    public Task<MemoryIndex> GetMemoryIndexAsync(CancellationToken ct = default)
    {
        return _indexManager.GetIndexAsync(ScopeKey, ct);
    }

    public string FormatRecallContext(MemoryRecallResult recall)
    {
        if (recall.WarmMemories.Count == 0 && recall.HotIndex.Entries.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine(MemoryContextStart);

        if (recall.HotIndex.Entries.Count > 0)
        {
            sb.AppendLine("Memory index:");
            foreach (var entry in recall.HotIndex.Entries)
            {
                var pitTag = entry.IsPointInTime ? " [snapshot]" : "";
                sb.AppendLine($"- [{entry.Type}]{pitTag} {entry.Title}: {entry.DescriptionHook}");
            }
            sb.AppendLine();
        }

        foreach (var warm in recall.WarmMemories)
        {
            var pitTag = warm.IsPointInTime ? " [snapshot]" : "";
            sb.AppendLine($"[{warm.Type}]{pitTag} {warm.Title}");
            if (!string.IsNullOrWhiteSpace(warm.Description))
                sb.AppendLine(warm.Description);
            if (!string.IsNullOrWhiteSpace(warm.Content))
                sb.AppendLine(warm.Content);

            // Show graph relationships if loaded
            if (warm.Relationships is { Count: > 0 })
            {
                sb.AppendLine("  Related:");
                foreach (var rel in warm.Relationships)
                    sb.AppendLine($"  - {rel.RelationshipType} → [{rel.RelatedEntityType}] {rel.RelatedEntityTitle}");
            }

            sb.AppendLine();
        }

        foreach (var warning in recall.FreshnessWarnings)
            sb.AppendLine(warning);

        sb.Append(MemoryContextEnd);
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Search, Forget, Update, Consolidate
    // ═══════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<MemorySearchResult>> SearchArchiveAsync(
        string query, int limit = 10, MemoryType? typeFilter = null,
        CancellationToken ct = default)
    {
        var embedding = await _store.GenerateEmbeddingAsync(query, ct);

        // Vector search on chunks (the only place embeddings live now)
        var results = await _store.VectorSearchAsync(ScopeKey, embedding, limit, typeFilter, ct);

        // Add freshness warnings
        foreach (var result in results)
        {
            var header = new MemoryHeader
            {
                MemoryId = result.Entry.Id,
                Title = result.Entry.Title,
                Type = result.Entry.Type,
                UpdatedAt = result.Entry.UpdatedAt,
                IsPointInTime = result.Entry.IsPointInTime
            };
            result.FreshnessWarning = _retriever.GetFreshnessWarning(header);
        }

        return results;
    }

    public async Task<MemoryConsolidationResult> ConsolidateAsync(CancellationToken ct = default)
    {
        var result = await _compactor.ConsolidateAsync(ScopeKey, ct);

        await _auditLog.RecordAsync("ScopeConsolidated", ScopeKey,
            summary: $"merged {result.DuplicatesMerged}, pruned {result.StaleMemoriesPruned}, " +
                     $"contradictions {result.ContradictionsResolved}, evicted {result.IndexEntriesEvicted}",
            actorId: ScopeKey, ct: ct);

        return result;
    }

    public async Task<bool> ForgetMemoryAsync(Guid memoryId, CancellationToken ct = default)
    {
        await _indexManager.RemoveIndexEntryAsync(ScopeKey, memoryId, ct);
        var deleted = await _store.DeleteEntityAsync(ScopeKey, memoryId, ct);

        if (deleted)
        {
            _logger.LogInformation("Forgot memory {Id} in scope '{Scope}'", memoryId, ScopeKey);
            await _auditLog.RecordAsync("MemoryForgotten", ScopeKey, memoryId, actorId: ScopeKey, ct: ct);
        }

        return deleted;
    }

    public async Task<MemoryEntry> UpdateMemoryAsync(
        Guid memoryId,
        string? title = null,
        MemoryType? type = null,
        string? content = null,
        string? description = null,
        MemoryTemperature? temperature = null,
        CancellationToken ct = default)
    {
        var existing = await _store.GetEntityByIdAsync(ScopeKey, memoryId, ct)
            ?? throw new InvalidOperationException($"Memory {memoryId} not found in scope '{ScopeKey}'");

        var effectiveType = type ?? existing.Type;
        if (content is not null)
        {
            var (isValid, reason) = MemoryTaxonomyRules.Validate(effectiveType, content, _options.AllowedMemoryTypes);
            if (!isValid)
                throw new InvalidOperationException($"Memory taxonomy validation failed: {reason}");
        }
        else if (type is not null && !_options.AllowedMemoryTypes.Contains(type.Value))
        {
            throw new InvalidOperationException($"Memory type '{type}' is not allowed for this configuration.");
        }

        // Update entity metadata — only the supplied fields change
        if (title is not null)
            existing.Title = title;
        existing.Type = effectiveType;
        if (description is not null)
            existing.Description = description;
        if (temperature is not null)
            existing.Temperature = temperature.Value;
        existing = await _store.UpdateEntityAsync(ScopeKey, existing, ct);

        // Update primary chunk content + embedding when content changed
        if (content is not null)
        {
            var chunk = await _store.GetPrimaryChunkAsync(ScopeKey, memoryId, ct);
            if (chunk is not null)
            {
                chunk.Content = content;
                try
                {
                    chunk.Embedding = await _store.GenerateEmbeddingAsync(
                        BuildEmbeddingText(existing.Title, existing.Description, content), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to regenerate embedding for memory '{Title}'", existing.Title);
                }
                await _store.UpdateChunkAsync(ScopeKey, chunk, ct);
            }
            existing.Content = content;
        }
        else
        {
            var chunk = await _store.GetPrimaryChunkAsync(ScopeKey, memoryId, ct);
            existing.Content = chunk?.Content;
        }

        // Update hot index
        await AddToHotIndexAsync(existing, ct);

        await _auditLog.RecordAsync("MemoryUpdated", ScopeKey, memoryId,
            summary: existing.Title, actorId: ScopeKey, ct: ct);

        return existing;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Extraction — with Entity Matching + Relationship Creation
    // ═══════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
        IList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        if (messages.Count == 0)
            return [];

        var chatClient = await GetChatClientAsync();
        if (chatClient is null)
        {
            _logger.LogWarning("No IChatClient available for memory extraction, skipping");
            return [];
        }

        var conversationText = string.Join("\n\n", messages.Select(m =>
        {
            var role = m.Role == ChatRole.User ? "User" : "Assistant";
            var text = StripMemoryContextMarkers(m.Text ?? "");
            return $"[{role}]: {text}";
        }).Where(s => s.Length > 10));

        if (string.IsNullOrWhiteSpace(conversationText))
            return [];

        var currentIndex = await _indexManager.GetIndexAsync(ScopeKey, ct);
        var existingMemories = currentIndex.Entries.Count > 0
            ? "\n\nAlready stored memories:\n" + string.Join("\n", currentIndex.Entries.Select(e =>
                $"- [{e.Type}] {e.Title}: {e.DescriptionHook}"))
            : "";

        var allowedTypes = string.Join(", ", _options.AllowedMemoryTypes);

        var systemPrompt = $"""
            You are a memory extraction agent. Your task is to identify durable knowledge from a conversation and return it as structured memories.

            A durable memory is something that will still be true and useful days, weeks, or months from now. It must stand the test of time.

            Classify each memory as exactly one of: {allowedTypes}
            - Fact: verified truths, domain knowledge, system behaviors, established states that rarely change
            - Rule: business rules, constraints, policies, conventions, conditions that govern decisions
            - Instruction: user directives, preferences, standing orders, explicit guidance that persists until revoked
            - Observation: patterns noticed, inferences, situational context that may become facts or become stale
            - Procedural: reusable workflows — ordered steps for accomplishing a class of task (e.g. "when the user asks to onboard a customer, do X then Y then Z"). Prefer this over Observation when the memory captures *how to do something* rather than *what is true*. The agent's SaveProcedure tool is the richer way to store these, but Procedural here is acceptable for a quick natural-language procedure.

            What qualifies as durable:
            - explicit user preferences or standing instructions
            - verified facts about systems, processes, or domain
            - business rules, constraints, or policies stated or confirmed
            - corrections to prior assumptions
            - stable environmental context (system quirks, integration behaviors, access boundaries)

            What does NOT qualify:
            - task progress, current work status, or in-flight details
            - transient observations tied to a specific moment
            - information already captured in the existing memories listed below
            - vague impressions without specific actionable content
            - anything that restates what was just asked or answered without adding durable context
            - content that was injected by the memory system itself (if you see memory-context markers, that content is already stored — skip it)

            Return a JSON object with a "memories" array. Each element:
            - "title": concise label, max 80 characters
            - "type": exactly one of [{allowedTypes}]
            - "content": the specific durable knowledge, stated as a standalone fact, rule, instruction, or observation
            - "description": one-line summary, max 120 characters. Must be consistent with the title — it should elaborate the title, never contradict it. If you cannot write a consistent pair, rewrite both.
            - "is_point_in_time": boolean. true if the memory is a snapshot of current state that will change (e.g. "Job 8 has 5 cans assigned", "allocation outcome for today's run", counts, IDs, current statuses). false for standing knowledge that does not decay (user instructions, business rules, stable domain facts). When in doubt for an Observation, prefer true. Instructions and Rules are almost always false.
            - "related_to": optional array of titles of OTHER memories in this batch that this memory relates to
              (e.g., if memory A is about Job 1 and memory B is about a plate assigned to Job 1, B should list A's title)

            If nothing qualifies, return an empty array. Prefer fewer high-confidence memories over many speculative ones.
            """;

        if (_options.PointInTimeMemories)
        {
            systemPrompt += """

                IMPORTANT: This agent works with live data sources. Most facts in the conversation
                are point-in-time snapshots (database query results, current statuses, counts) that
                become stale immediately. Prefer extracting ONLY:
                - User instructions and preferences (durable)
                - System rules and constraints (change rarely)
                - Domain knowledge that is truly stable
                Do NOT extract query results, current values, statuses, or counts as Facts.
                If you must capture a transient observation, classify it as Observation.
                """;
        }

        var userPrompt = $"""
            Review this conversation and extract durable memories.

            Conversation:
            {conversationText}
            {existingMemories}
            """;

        try
        {
            var llmMessages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var response = await chatClient.GetResponseAsync(llmMessages, cancellationToken: ct);
            var responseText = response.Text ?? "";

            var extracted = ParseExtractedMemories(responseText);
            if (extracted.Count == 0)
            {
                _logger.LogDebug("No memories extracted from conversation for agent '{Agent}'", ScopeKey);
                return [];
            }

            // Save each extracted memory (entity matching happens inside SaveMemoryAsync)
            var savedMap = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var mem in extracted)
            {
                try
                {
                    var entry = await SaveMemoryAsync(mem.Title, mem.Type, mem.Content, mem.Description,
                        isPointInTime: mem.IsPointInTime || _options.PointInTimeMemories, ct: ct);
                    savedMap[mem.Title] = entry;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save extracted memory '{Title}'", mem.Title);
                }
            }

            // Create relationships between extracted memories
            if (_options.Consolidation.EnableRelationshipExtraction)
            {
                foreach (var mem in extracted)
                {
                    if (mem.RelatedTo is not { Count: > 0 }) continue;
                    if (!savedMap.TryGetValue(mem.Title, out var fromEntity)) continue;

                    foreach (var relatedTitle in mem.RelatedTo)
                    {
                        if (savedMap.TryGetValue(relatedTitle, out var toEntity))
                        {
                            try
                            {
                                await _store.InsertRelationshipAsync(
                                    ScopeKey, fromEntity.Id, toEntity.Id,
                                    "related_to",
                                    $"{fromEntity.Title} relates to {toEntity.Title}",
                                    ct: ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to create relationship: {From} → {To}",
                                    fromEntity.Title, toEntity.Title);
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("Extracted {Count} memories from conversation in scope '{Scope}'",
                savedMap.Count, ScopeKey);

            if (savedMap.Count > 0)
            {
                await _auditLog.RecordAsync("MemoriesExtracted", ScopeKey,
                    summary: $"{savedMap.Count} memories extracted from conversation",
                    actorId: ScopeKey, ct: ct);
            }

            return savedMap.Values.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction failed for agent '{Agent}'", ScopeKey);
            return [];
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private Helpers
    // ═══════════════════════════════════════════════════════════════════

    private async Task AddToHotIndexAsync(MemoryEntry entry, CancellationToken ct)
    {
        var indexEntry = new MemoryIndexEntry
        {
            MemoryId = entry.Id,
            Title = entry.Title,
            Type = entry.Type,
            DescriptionHook = TruncateHook(entry.Description ?? entry.Title),
            UpdatedAt = entry.UpdatedAt,
            IsPointInTime = entry.IsPointInTime
        };
        await _indexManager.AddIndexEntryAsync(ScopeKey, indexEntry, ct);
    }

    private async Task EnsureScopeRegisteredAsync(CancellationToken ct)
    {
        if (_scopeEnsured) return;

        try
        {
            await _scopeService.EnsureScopeAsync(ScopeKey, isShared: false, ct);
            _scopeEnsured = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scope auto-registration failed for '{Scope}' — will retry on next save", ScopeKey);
        }
    }

    private async Task TryAutoConsolidateAsync(CancellationToken ct)
    {
        if (!_options.Consolidation.EnableAutoConsolidation) return;

        // Gate: never run two consolidations concurrently on the same scope.
        if (Interlocked.CompareExchange(ref _consolidationRunning, 1, 0) != 0) return;

        var release = true;
        try
        {
            var cap = _options.Consolidation.MemoryFileCap;
            var allHeaders = await _store.GetHeadersAsync(ScopeKey, cap + 1, ct: ct);
            if (allHeaders.Count > cap)
            {
                _logger.LogInformation("Memory count ({Count}) exceeds cap ({Cap}), triggering auto-consolidation",
                    allHeaders.Count, cap);

                release = false; // released by the background task
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ConsolidateAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auto-consolidation failed for scope '{Scope}'", ScopeKey);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _consolidationRunning, 0);
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            if (release)
                Interlocked.Exchange(ref _consolidationRunning, 0);
        }
    }

    private async Task<string?> TryMergeContentAsync(string existingContent, string newContent, CancellationToken ct)
    {
        // Content merges are creative writing — route via Large tier when configured.
        var chatClient = await GetChatClientAsync(LlmModelTier.Large);
        if (chatClient is null)
            return null;

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, """
                    You are a knowledge merger. Given an existing memory and new information about the same topic,
                    produce a single updated memory that incorporates both. Use the new information as the primary
                    source (it's more recent), but keep any unique details from the existing memory that aren't
                    contradicted by the new information. Be concise — this is a memory entry, not a document.
                    Return ONLY the merged content text, nothing else.
                    """),
                new(ChatRole.User, $"Existing memory:\n{existingContent}\n\nNew information:\n{newContent}")
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
            return response.Text?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM content merge failed, using new content as replacement");
            return null;
        }
    }

    private static string BuildEmbeddingText(string title, string? description, string content)
    {
        var text = $"{title}. {description}";
        if (!string.IsNullOrWhiteSpace(content))
            text += $" {content}";
        return text;
    }

    private Task<IChatClient?> GetChatClientAsync() =>
        GetChatClientAsync(LlmModelTier.Default);

    private async Task<IChatClient?> GetChatClientAsync(LlmModelTier tier)
    {
        try
        {
            var chatClientService = _serviceProvider.GetService<IFabrCoreChatClientService>();
            if (chatClientService is null)
                return null;

            var modelName = _options.Models.ResolveModelForCall(tier, _options.Models.CompactionModelName);
            return await chatClientService.GetChatClient(modelName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve chat client for tier {Tier}", tier);
            return null;
        }
    }

    private static string TruncateHook(string text, int maxLength = 120)
    {
        if (text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static string StripMemoryContextMarkers(string text)
    {
        if (!text.Contains(MemoryContextStart))
            return text;

        return MemoryContextPattern().Replace(text, "").Trim();
    }

    [GeneratedRegex(
        @"<memory-context source=""agent-memory-system"">.*?</memory-context>",
        RegexOptions.Singleline)]
    private static partial Regex MemoryContextPattern();

    // ─── Extraction Parser ──────────────────────────────────────────

    private record ExtractedMemory(
        string Title, MemoryType Type, string Content, string Description,
        bool IsPointInTime, List<string>? RelatedTo);

    private static List<ExtractedMemory> ParseExtractedMemories(string responseText)
    {
        try
        {
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return [];

            var json = responseText[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("memories", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<ExtractedMemory>();
            foreach (var item in arr.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var typeStr = item.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
                var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;

                if (title is null || content is null || typeStr is null)
                    continue;

                if (!Enum.TryParse<MemoryType>(typeStr, ignoreCase: true, out var type))
                    continue;

                var isPointInTime = item.TryGetProperty("is_point_in_time", out var pit)
                    && pit.ValueKind == JsonValueKind.True;

                // Parse related_to array
                List<string>? relatedTo = null;
                if (item.TryGetProperty("related_to", out var relArr) && relArr.ValueKind == JsonValueKind.Array)
                {
                    relatedTo = [];
                    foreach (var rel in relArr.EnumerateArray())
                    {
                        var relTitle = rel.GetString();
                        if (!string.IsNullOrWhiteSpace(relTitle))
                            relatedTo.Add(relTitle);
                    }
                }

                results.Add(new ExtractedMemory(title, type, content, description ?? title, isPointInTime, relatedTo));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }
}
