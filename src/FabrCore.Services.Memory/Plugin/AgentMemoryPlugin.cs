using System.ComponentModel;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Plugin;

/// <summary>
/// FabrCore plugin that exposes agent memory operations as LLM-callable tools.
/// Provides save, recall, search, forget, index, and consolidation tools.
/// All operations are bound to one memory scope — the agent's own handle by default
/// (isolated memory), or a named shared scope when configured via the plugin setting
/// or arg <c>MemoryScope</c>.
/// </summary>
[PluginAlias("agent-memory")]
public class AgentMemoryPlugin : IFabrCorePlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly string MemoryTypeNames = string.Join(", ", Enum.GetNames<MemoryType>());

    private IAgentMemoryService? _memoryService;
    private IMemorySummaryTree? _summaryTree;
    private string? _boundScopeKey;
    private ILogger _logger = default!;

    /// <summary>
    /// The memory scope all operations bind to. Optional — when unset, the scope is
    /// resolved from the plugin setting / arg <c>MemoryScope</c>, falling back to the
    /// agent handle (isolated memory).
    /// </summary>
    public string? MemoryScope { get; set; }

    /// <summary>Legacy alias for <see cref="MemoryScope"/>.</summary>
    [Obsolete("Use MemoryScope instead. AgentHandle will be removed in a future release.")]
    public string? AgentHandle
    {
        get => MemoryScope;
        set => MemoryScope = value;
    }

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<AgentMemoryPlugin>();

        var scopeKey = MemoryScopeResolver.Resolve(config, MemoryScope);

        var provider = serviceProvider.GetRequiredService<IAgentMemoryProvider>();
        _memoryService = provider.GetMemoryService(scopeKey);
        _summaryTree = serviceProvider.GetService<IMemorySummaryTree>();
        _boundScopeKey = scopeKey;

        _logger.LogInformation("AgentMemoryPlugin initialized for scope '{ScopeKey}'", scopeKey);
        return Task.CompletedTask;
    }

    private IAgentMemoryService RequireService() =>
        _memoryService ?? throw new InvalidOperationException(
            "AgentMemoryPlugin has not been initialized. Call InitializeAsync first.");

    // ─── LLM Tools ──────────────────────────────────────────────────────

    [Description("Save a memory to the structured memory store. Store durable knowledge that will still be true and useful across future conversations. The store may be shared with other agents when a shared memory scope is configured.")]
    public async Task<string> SaveMemory(
        [Description("A short descriptive title for the memory")] string title,
        [Description("Memory type: 'Fact' (verified truths, domain knowledge), 'Rule' (business rules, constraints, policies), 'Instruction' (user directives, standing orders), 'Observation' (patterns noticed, inferences, situational context), or 'Procedural' (workflow patterns — prefer SaveProcedure for structured steps)")] string type,
        [Description("Full content/details of the memory")] string content,
        [Description("Optional brief description (defaults to the title)")] string? description = null,
        [Description("Set to true if this memory is a point-in-time snapshot (e.g., database query result) that may be stale immediately")] bool isPointInTime = false)
    {
        try
        {
            if (!Enum.TryParse<MemoryType>(type, ignoreCase: true, out var memoryType))
                return $"Error: Invalid memory type '{type}'. Must be one of: {MemoryTypeNames}.";

            var entry = await RequireService().SaveMemoryAsync(title, memoryType, content, description, isPointInTime: isPointInTime);
            return JsonSerializer.Serialize(new
            {
                memoryId = entry.Id,
                title = entry.Title,
                type = entry.Type.ToString(),
                message = "Memory saved successfully."
            }, JsonOptions);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("taxonomy"))
        {
            _logger.LogWarning("Memory rejected by taxonomy: {Reason}", ex.Message);
            return $"Memory rejected: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveMemory failed for '{Title}'", title);
            return $"Error saving memory: {ex.Message}";
        }
    }

    [Description("Save a procedure — a reusable, ordered workflow the agent can recall later when it needs to " +
                 "perform this class of task. Use this for repeatable multi-step flows with clear trigger conditions " +
                 "(e.g., 'onboard a new customer': query table, validate fields, send welcome email). " +
                 "Steps should be specific and ordered. Parameters that vary per invocation stay out of the step text.")]
    public async Task<string> SaveProcedure(
        [Description("Short descriptive title for the procedure (e.g., 'Onboard new customer')")] string title,
        [Description("When to apply this procedure (e.g., 'User asks to add a customer to the system')")] string triggerCondition,
        [Description("Ordered steps as JSON array. Each item: {\"order\": 1, \"action\": \"...\", \"description\": \"...\", \"expectedOutcome\": \"...\", \"tool\": \"...\"}. Only 'order' and 'action' are required.")] string stepsJson,
        [Description("Optional: JSON array of tool names the agent should prefer (e.g., [\"customer-plugin\",\"email\"])")] string? preferredToolsJson = null,
        [Description("Optional narrative description of the procedure")] string? description = null)
    {
        try
        {
            ProceduralSteps procedure;
            try
            {
                var stepsDoc = JsonDocument.Parse(stepsJson);
                if (stepsDoc.RootElement.ValueKind != JsonValueKind.Array)
                    return "Error: stepsJson must be a JSON array.";

                var parsedSteps = new List<ProcedureStep>();
                foreach (var el in stepsDoc.RootElement.EnumerateArray())
                {
                    var order = el.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number
                        ? o.GetInt32() : parsedSteps.Count + 1;
                    var action = el.TryGetProperty("action", out var a) ? a.GetString() : null;
                    if (string.IsNullOrWhiteSpace(action))
                        continue;

                    parsedSteps.Add(new ProcedureStep
                    {
                        Order = order,
                        Action = action,
                        Description = el.TryGetProperty("description", out var d) ? d.GetString() : null,
                        ExpectedOutcome = el.TryGetProperty("expectedOutcome", out var eo)
                            ? eo.GetString()
                            : (el.TryGetProperty("expected_outcome", out var eo2) ? eo2.GetString() : null),
                        Tool = el.TryGetProperty("tool", out var tl) ? tl.GetString() : null
                    });
                }

                if (parsedSteps.Count == 0)
                    return "Error: stepsJson contained no valid steps (each step requires an 'action').";

                List<string>? preferredTools = null;
                if (!string.IsNullOrWhiteSpace(preferredToolsJson))
                {
                    try
                    {
                        preferredTools = JsonSerializer.Deserialize<List<string>>(preferredToolsJson);
                    }
                    catch { preferredTools = null; }
                }

                procedure = new ProceduralSteps
                {
                    TriggerCondition = triggerCondition,
                    Steps = parsedSteps.OrderBy(s => s.Order).ToList(),
                    PreferredTools = preferredTools
                };
            }
            catch (JsonException jx)
            {
                return $"Error: stepsJson is not valid JSON — {jx.Message}";
            }

            // Build a human-readable narrative for Content so the LLM sees the procedure naturally.
            var narrative = BuildProcedureNarrative(triggerCondition, procedure);
            var metadata = new Dictionary<string, string>
            {
                [ProceduralSteps.MetadataKey] = procedure.ToJson()
            };

            var entry = await RequireService().SaveMemoryAsync(
                title: title,
                type: MemoryType.Procedural,
                content: narrative,
                description: description ?? $"Procedure: {triggerCondition}",
                metadata: metadata,
                isPointInTime: false);

            return JsonSerializer.Serialize(new
            {
                memoryId = entry.Id,
                title = entry.Title,
                type = entry.Type.ToString(),
                stepCount = procedure.Steps.Count,
                message = "Procedure saved successfully."
            }, JsonOptions);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("taxonomy"))
        {
            return $"Procedure rejected: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveProcedure failed for '{Title}'", title);
            return $"Error saving procedure: {ex.Message}";
        }
    }

    private static string BuildProcedureNarrative(string triggerCondition, ProceduralSteps procedure)
    {
        var lines = new List<string>
        {
            $"Trigger: {triggerCondition}",
            "Steps:"
        };
        foreach (var step in procedure.Steps)
        {
            var line = $"  {step.Order}. {step.Action}";
            if (!string.IsNullOrWhiteSpace(step.Tool))
                line += $" [tool: {step.Tool}]";
            lines.Add(line);
            if (!string.IsNullOrWhiteSpace(step.Description))
                lines.Add($"     • {step.Description}");
            if (!string.IsNullOrWhiteSpace(step.ExpectedOutcome))
                lines.Add($"     → {step.ExpectedOutcome}");
        }
        if (procedure.PreferredTools is { Count: > 0 })
            lines.Add($"Preferred tools: {string.Join(", ", procedure.PreferredTools)}");
        return string.Join("\n", lines);
    }

    [Description("Recall relevant memories for the current query. Returns the always-loaded memory index " +
                 "plus selectively retrieved memories with freshness warnings for stale entries. " +
                 "The memory pool may be shared with other agents when a shared scope is configured. " +
                 "Call this before answering questions that may depend on prior context.")]
    public async Task<string> RecallMemories(
        [Description("The current query or topic to find relevant memories for")] string query)
    {
        try
        {
            var result = await RequireService().RecallAsync(query);

            return JsonSerializer.Serialize(new
            {
                hotIndex = new
                {
                    entryCount = result.HotIndex.Entries.Count,
                    estimatedTokens = result.HotIndex.TotalEstimatedTokens,
                    entries = result.HotIndex.Entries.Select(e => new
                    {
                        memoryId = e.MemoryId,
                        title = e.Title,
                        type = e.Type.ToString(),
                        hook = e.DescriptionHook,
                        updatedAt = e.UpdatedAt
                    })
                },
                warmMemories = result.WarmMemories.Select(m => new
                {
                    memoryId = m.Id,
                    title = m.Title,
                    type = m.Type.ToString(),
                    description = m.Description,
                    content = m.Content,
                    updatedAt = m.UpdatedAt
                }),
                freshnessWarnings = result.FreshnessWarnings,
                warmMemoryCount = result.WarmMemories.Count
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RecallMemories failed for query: {Query}", query);
            return $"Error recalling memories: {ex.Message}";
        }
    }

    [Description("Search the cold layer archive for memories via vector similarity. " +
                 "Use for finding older or archived information not in the hot index.")]
    public async Task<string> SearchArchive(
        [Description("The search query")] string query,
        [Description("Maximum results to return (default 10)")] int limit = 10,
        [Description("Optional filter by type: 'Fact', 'Rule', 'Instruction', 'Observation', or 'Procedural'")] string? typeFilter = null)
    {
        try
        {
            MemoryType? memType = null;
            if (typeFilter is not null && Enum.TryParse<MemoryType>(typeFilter, ignoreCase: true, out var parsed))
                memType = parsed;

            var results = await RequireService().SearchArchiveAsync(query, limit, memType);

            return JsonSerializer.Serialize(new
            {
                results = results.Select(r => new
                {
                    memoryId = r.Entry.Id,
                    title = r.Entry.Title,
                    type = r.Entry.Type.ToString(),
                    description = r.Entry.Description,
                    content = r.Entry.Content,
                    distance = r.Distance,
                    freshnessWarning = r.FreshnessWarning,
                    updatedAt = r.Entry.UpdatedAt
                }),
                count = results.Count
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchArchive failed for query: {Query}", query);
            return $"Error searching archive: {ex.Message}";
        }
    }

    [Description("Delete a memory by its ID. Removes it from both the store and the hot layer index.")]
    public async Task<string> ForgetMemory(
        [Description("The GUID of the memory to delete")] string memoryId)
    {
        try
        {
            if (!Guid.TryParse(memoryId, out var id))
                return $"Error: Invalid memory ID format '{memoryId}'.";

            var deleted = await RequireService().ForgetMemoryAsync(id);
            return deleted
                ? JsonSerializer.Serialize(new { memoryId = id, message = "Memory forgotten successfully." }, JsonOptions)
                : $"Memory {memoryId} not found.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ForgetMemory failed for ID: {MemoryId}", memoryId);
            return $"Error forgetting memory: {ex.Message}";
        }
    }

    [Description("Get the hot layer memory index — the always-loaded table of contents of agent memories. " +
                 "Shows all indexed memories with their titles, types, and description hooks.")]
    public async Task<string> GetMemoryIndex()
    {
        try
        {
            var index = await RequireService().GetMemoryIndexAsync();

            return JsonSerializer.Serialize(new
            {
                entryCount = index.Entries.Count,
                estimatedTokens = index.TotalEstimatedTokens,
                entries = index.Entries.Select(e => new
                {
                    memoryId = e.MemoryId,
                    title = e.Title,
                    type = e.Type.ToString(),
                    hook = e.DescriptionHook,
                    updatedAt = e.UpdatedAt
                })
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMemoryIndex failed");
            return $"Error getting memory index: {ex.Message}";
        }
    }

    [Description("Query the hierarchical semantic summary tree for topic-level rollups relevant to the query. " +
                 "Use for broad questions (\"what do you know about X\", \"summarize our work on Y\") where a " +
                 "topic summary is more useful than a handful of individual memories. Returns empty when the " +
                 "summary tree has not been built (run ConsolidateMemories first) or the feature is disabled.")]
    public async Task<string> QuerySummaries(
        [Description("The broad topic or question to resolve against the summary tree")] string query,
        [Description("Maximum number of summary nodes to return (default 5)")] int limit = 5)
    {
        try
        {
            if (_summaryTree is null || _boundScopeKey is null)
                return JsonSerializer.Serialize(new { summaries = Array.Empty<object>(), count = 0, message = "Summary tree unavailable." }, JsonOptions);

            var nodes = await _summaryTree.QueryAsync(_boundScopeKey, query, limit);
            return JsonSerializer.Serialize(new
            {
                summaries = nodes.Select(n => new
                {
                    nodeId = n.NodeId,
                    topic = n.Topic,
                    summary = n.Summary,
                    depth = n.Depth,
                    memberCount = n.MemberCount,
                    updatedAt = n.UpdatedAt
                }),
                count = nodes.Count
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuerySummaries failed for query: {Query}", query);
            return $"Error querying summaries: {ex.Message}";
        }
    }

    [Description("Run memory consolidation: merge duplicates, archive stale observations, resolve contradictions, " +
                 "and enforce index budgets. Use when memory quality is degrading or the store is growing large.")]
    public async Task<string> ConsolidateMemories()
    {
        try
        {
            var result = await RequireService().ConsolidateAsync();

            return JsonSerializer.Serialize(new
            {
                duplicatesMerged = result.DuplicatesMerged,
                staleMemoriesPruned = result.StaleMemoriesPruned,
                contradictionsResolved = result.ContradictionsResolved,
                indexEntriesEvicted = result.IndexEntriesEvicted,
                consolidatedAt = result.ConsolidatedAt,
                message = "Consolidation complete."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConsolidateMemories failed");
            return $"Error consolidating memories: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
