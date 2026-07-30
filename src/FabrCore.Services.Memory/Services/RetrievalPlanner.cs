using System.Text.Json;
using FabrCore.Core;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Default <see cref="IRetrievalPlanner"/> implementation. Prefers cheap deterministic heuristics
/// and only escalates to an LLM classification call when the heuristic cannot confidently pick a plan.
/// </summary>
internal class RetrievalPlanner : IRetrievalPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Queries shorter than this (in characters) are treated as trivial.</summary>
    private const int TrivialQueryLengthThreshold = 15;

    /// <summary>Temporal markers that bias the plan toward deeper retrieval.</summary>
    private static readonly string[] TemporalMarkers =
    [
        "last time", "recently", "yesterday", "last week", "earlier", "before",
        "prior", "previously", "history", "over time", "in the past"
    ];

    /// <summary>Markers that usually indicate the agent wants to act on a known procedure.</summary>
    private static readonly string[] ActionMarkers =
    [
        "how do i", "how to", "steps to", "walk me through", "run", "execute",
        "procedure", "workflow", "process for"
    ];

    /// <summary>Markers that signal a broad topic-level query that a summary tree can answer cheaply.</summary>
    private static readonly string[] BroadTopicMarkers =
    [
        "summarize", "overview", "what do you know about", "tell me about",
        "what have we learned", "recap"
    ];

    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RetrievalPlanner> _logger;

    public RetrievalPlanner(
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<RetrievalPlanner>();
    }

    public async Task<RetrievalPlan> CreatePlanAsync(
        string query,
        MemoryIndex hotIndex,
        CancellationToken ct = default)
    {
        if (!_options.Retrieval.PlannerEnabled)
        {
            return new RetrievalPlan
            {
                Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand],
                Source = RetrievalPlanSource.Disabled,
                Rationale = "Planner disabled via options"
            };
        }

        var normalizedQuery = query?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return RetrievalPlan.HotIndexOnly("Empty query — hot index only");
        }

        var heuristicPlan = TryHeuristicPlan(normalizedQuery, hotIndex);
        if (heuristicPlan is not null)
        {
            _logger.LogDebug("Retrieval planner (heuristic): {Steps} — {Rationale}",
                string.Join(",", heuristicPlan.Steps), heuristicPlan.Rationale);
            return heuristicPlan;
        }

        try
        {
            var llmPlan = await LlmClassifyAsync(normalizedQuery, hotIndex, ct);
            if (llmPlan is not null)
            {
                _logger.LogDebug("Retrieval planner (LLM): {Steps} — {Rationale}",
                    string.Join(",", llmPlan.Steps), llmPlan.Rationale);
                return llmPlan;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Retrieval planner LLM classification failed, falling back to Standard plan");
        }

        return RetrievalPlan.Standard("LLM classification unavailable or failed — using Standard plan");
    }

    // ─── Heuristics ─────────────────────────────────────────────────────

    private RetrievalPlan? TryHeuristicPlan(string query, MemoryIndex hotIndex)
    {
        // Trivial queries: too short to warrant selection; hot index usually covers them.
        if (query.Length < TrivialQueryLengthThreshold)
            return RetrievalPlan.HotIndexOnly($"Query length {query.Length} < {TrivialQueryLengthThreshold}");

        var queryLower = query.ToLowerInvariant();

        // Broad-topic markers and the summary tree is built: answer from summaries, skip header scan.
        if (_options.SummaryTree.Enabled && BroadTopicMarkers.Any(m => queryLower.Contains(m)))
        {
            return new RetrievalPlan
            {
                Steps = [RetrievalStep.SummaryTreeScan, RetrievalStep.HeaderScanLlmSelect],
                Source = RetrievalPlanSource.Heuristic,
                Rationale = "Broad-topic marker — summary tree scan + fallback selection"
            };
        }

        // Temporal markers — user is asking about history; reach into archive.
        if (TemporalMarkers.Any(m => queryLower.Contains(m)))
        {
            return new RetrievalPlan
            {
                Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand, RetrievalStep.ArchiveSearch],
                Source = RetrievalPlanSource.Heuristic,
                Rationale = "Temporal marker — include archive search"
            };
        }

        // Action/procedure markers — bias toward Procedural + Instruction types.
        if (ActionMarkers.Any(m => queryLower.Contains(m)))
        {
            return new RetrievalPlan
            {
                Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand],
                PreferredTypes = [MemoryType.Procedural, MemoryType.Instruction, MemoryType.Rule],
                Source = RetrievalPlanSource.Heuristic,
                Rationale = "Action marker — bias toward Procedural/Instruction/Rule"
            };
        }

        // Hot-index coverage: if query tokens strongly overlap a single hot-index entry's title/hook,
        // the hot index already answers the query — return HotIndexOnly.
        if (HotIndexStronglyCovers(queryLower, hotIndex))
        {
            return RetrievalPlan.HotIndexOnly("Hot index already covers the query");
        }

        // Inconclusive — defer to the LLM (or Standard fallback if LLM unavailable).
        return null;
    }

    private static bool HotIndexStronglyCovers(string queryLower, MemoryIndex hotIndex)
    {
        if (hotIndex.Entries.Count == 0)
            return false;

        var queryTokens = queryLower
            .Split([' ', '\t', '\n', '.', ',', '?', '!', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3)
            .ToHashSet();

        if (queryTokens.Count == 0)
            return false;

        foreach (var entry in hotIndex.Entries)
        {
            var entryText = $"{entry.Title} {entry.DescriptionHook}".ToLowerInvariant();
            var entryTokens = entryText
                .Split([' ', '\t', '\n', '.', ',', '?', '!', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 3)
                .ToHashSet();

            if (entryTokens.Count == 0)
                continue;

            var overlap = queryTokens.Intersect(entryTokens).Count();
            // Strong match: ≥2 shared meaningful tokens AND ≥60% of the query's tokens matched.
            if (overlap >= 2 && (double)overlap / queryTokens.Count >= 0.6)
                return true;
        }

        return false;
    }

    // ─── LLM classification ─────────────────────────────────────────────

    private async Task<RetrievalPlan?> LlmClassifyAsync(string query, MemoryIndex hotIndex, CancellationToken ct)
    {
        var chatClient = await GetChatClientAsync();
        if (chatClient is null)
            return null;

        var indexSummary = hotIndex.Entries.Count == 0
            ? "(empty)"
            : string.Join("\n", hotIndex.Entries.Take(12).Select(e => $"- [{e.Type}] {e.Title}: {e.DescriptionHook}"));

        var systemPrompt = """
            You are a retrieval planner. Classify the user's query into one of three tiers based on
            how much memory-system work is needed to answer it well.

            Tiers:
            - "hot_only": the current hot index entries (already loaded, shown to you below) are
              sufficient, or the query is trivial/conversational and doesn't need memory lookup.
            - "standard": the query needs a header scan + LLM relevance selection + graph expansion.
              This is the default — pick it whenever you can't justify hot_only or deep.
            - "deep": the query reaches back into older knowledge — temporal references, audits,
              "what did we used to do", or topics unlikely to be in the hot index. Includes archive search.

            Return strict JSON: {"tier": "hot_only" | "standard" | "deep", "rationale": "one short sentence"}.
            Prefer standard when uncertain. Never invent a fourth tier.
            """;

        var userPrompt = $"""
            Query: {query}

            Hot index (currently loaded):
            {indexSummary}

            Classify the tier.
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        return ParseLlmPlan(response.Text ?? "");
    }

    private static RetrievalPlan? ParseLlmPlan(string responseText)
    {
        try
        {
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return null;

            var json = responseText[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tier", out var tierProp))
                return null;

            var tier = tierProp.GetString()?.Trim().ToLowerInvariant();
            var rationale = doc.RootElement.TryGetProperty("rationale", out var rProp)
                ? rProp.GetString()
                : null;

            return tier switch
            {
                "hot_only" => new RetrievalPlan
                {
                    Steps = [RetrievalStep.HotIndexOnly],
                    Source = RetrievalPlanSource.Llm,
                    Rationale = rationale ?? "LLM: hot_only"
                },
                "deep" => new RetrievalPlan
                {
                    Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand, RetrievalStep.ArchiveSearch],
                    Source = RetrievalPlanSource.Llm,
                    Rationale = rationale ?? "LLM: deep"
                },
                "standard" or _ => new RetrievalPlan
                {
                    Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand],
                    Source = RetrievalPlanSource.Llm,
                    Rationale = rationale ?? "LLM: standard"
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<IChatClient?> GetChatClientAsync()
    {
        try
        {
            var chatClientService = _serviceProvider.GetService<IFabrCoreChatClientService>();
            if (chatClientService is null)
                return null;

            // Planner is a cheap classification — route through the Small tier, with the planner-specific
            // name as the explicit override, falling back to the relevance name when the planner name is blank.
            var explicitName = !string.IsNullOrWhiteSpace(_options.Models.PlannerModelName)
                ? _options.Models.PlannerModelName
                : _options.Models.RelevanceModelName;
            var modelName = _options.Models.ResolveModelForCall(LlmModelTier.Small, explicitName);

            return await chatClientService.GetChatClient(modelName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve chat client for retrieval planner");
            return null;
        }
    }
}
