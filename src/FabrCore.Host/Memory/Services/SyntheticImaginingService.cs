using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Analyzes conversation context via an LLM to generate targeted memory search queries,
/// runs them through the agent memory system in parallel, and returns aggregated,
/// deduplicated results.
/// </summary>
internal partial class SyntheticImaginingService : ISyntheticImaginingService
{
    /// <summary>Maximum number of recent messages to include in the conversation context sent to the LLM.</summary>
    private const int MaxContextMessages = 20;

    private readonly IAgentMemoryProvider _memoryProvider;
    private readonly AgentMemoryOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyntheticImaginingService> _logger;

    public SyntheticImaginingService(
        IAgentMemoryProvider memoryProvider,
        AgentMemoryOptions options,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _memoryProvider = memoryProvider;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<SyntheticImaginingService>();
    }

    public async Task<SyntheticImaginingResult> ImagineAsync(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        string lastUserMessage,
        string scopeKey,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default)
    {
        // Read messages from the provider — this is read-only analysis,
        // so no fork needed (we never write back to the history)
        var messages = (IList<ChatMessage>)await chatHistoryProvider.GetMessagesAsync(ct);

        return await ImagineAsync(messages, lastUserMessage, scopeKey, alreadySurfacedIds, ct);
    }

    public async Task<SyntheticImaginingResult> ImagineAsync(
        IList<ChatMessage> messages,
        string lastUserMessage,
        string scopeKey,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lastUserMessage))
            return new SyntheticImaginingResult { Success = true };

        try
        {
            // Step 1: Generate search queries from conversation context
            var queries = await GenerateQueriesAsync(messages, lastUserMessage, ct);
            if (queries.Count == 0)
            {
                _logger.LogDebug("Synthetic imagining generated no queries for agent '{Agent}'", scopeKey);
                return new SyntheticImaginingResult { Success = true };
            }

            _logger.LogInformation(
                "Synthetic imagining generated {Count} queries for agent '{Agent}': {Queries}",
                queries.Count, scopeKey, string.Join(" | ", queries));

            // Step 2: Run all queries through memory search in parallel
            var memoryService = _memoryProvider.GetMemoryService(scopeKey);
            var result = await ExecuteQueriesAsync(memoryService, queries, alreadySurfacedIds, ct);
            result.GeneratedQueries = queries;

            _logger.LogInformation(
                "Synthetic imagining complete for agent '{Agent}': {Unique} unique memories from {Queries} queries",
                scopeKey, result.UniqueMemoryCount, queries.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Synthetic imagining failed for agent '{Agent}'", scopeKey);
            return new SyntheticImaginingResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    // ─── Query Generation ──────────────────────────────────────────

    private async Task<List<string>> GenerateQueriesAsync(
        IList<ChatMessage> messages,
        string lastUserMessage,
        CancellationToken ct)
    {
        var chatClient = await GetChatClientAsync();
        if (chatClient is null)
        {
            _logger.LogWarning("No IChatClient available for synthetic imagining, skipping");
            return [];
        }

        var conversationText = BuildConversationText(messages);

        var systemPrompt = $$"""
            You are a memory retrieval strategist. Analyze the conversation and the user's latest message to generate search queries that will find relevant agent memories.

            Consider:
            - Direct topic matches for what the user is asking about
            - Implicit context needs (domain knowledge, system behaviors)
            - Applicable user preferences or standing instructions
            - Relevant rules, constraints, or policies
            - Prior observations that provide useful situational context

            Generate 1-{{_options.Retrieval.MaxImaginingQueries}} diverse queries. Each query should target a different aspect of what might be relevant. Avoid redundant queries that would return the same results.

            Return a JSON object: {"queries": ["query1", "query2", ...]}
            Return {"queries": []} if the conversation is trivial or no memory search would help.
            """;

        var userPrompt = $"""
            Latest user message: {lastUserMessage}

            Recent conversation context:
            {conversationText}

            Generate memory search queries.
            """;

        var llmMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await chatClient.GetResponseAsync(llmMessages, cancellationToken: ct);
        var responseText = response.Text ?? "";

        return ParseQueries(responseText);
    }

    private string BuildConversationText(IList<ChatMessage> messages)
    {
        // Take the last N messages to keep within token budget
        var recentMessages = messages.Count > MaxContextMessages
            ? messages.Skip(messages.Count - MaxContextMessages).ToList()
            : messages;

        var sb = new StringBuilder();
        foreach (var msg in recentMessages)
        {
            var role = msg.Role == ChatRole.User ? "User" : "Assistant";
            var text = StripMemoryContextMarkers(msg.Text ?? "");
            if (text.Length > 10) // Skip empty/trivial messages
                sb.AppendLine($"[{role}]: {text}");
        }

        return sb.ToString();
    }

    private List<string> ParseQueries(string responseText)
    {
        try
        {
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return [];

            var json = responseText[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("queries", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];

            var queries = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                var query = item.GetString();
                if (!string.IsNullOrWhiteSpace(query))
                    queries.Add(query);
            }

            // Cap at configured maximum
            if (queries.Count > _options.Retrieval.MaxImaginingQueries)
                queries = queries.Take(_options.Retrieval.MaxImaginingQueries).ToList();

            return queries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse imagining query response");
            return [];
        }
    }

    // ─── Query Execution & Deduplication ───────────────────────────

    private async Task<SyntheticImaginingResult> ExecuteQueriesAsync(
        IAgentMemoryService memoryService,
        List<string> queries,
        IReadOnlySet<Guid>? alreadySurfacedIds,
        CancellationToken ct)
    {
        // Run all recall + archive searches in parallel
        var recallTasks = queries.Select(q =>
            memoryService.RecallAsync(q, alreadySurfacedIds, ct: ct)).ToList();
        var archiveTasks = queries.Select(q =>
            memoryService.SearchArchiveAsync(q, ct: ct)).ToList();

        await Task.WhenAll(
            Task.WhenAll(recallTasks),
            Task.WhenAll(archiveTasks));

        // Deduplicate recall results
        var seenWarmIds = new HashSet<Guid>();
        var aggregatedRecall = new MemoryRecallResult();
        var allArchiveResults = new Dictionary<Guid, MemorySearchResult>();

        for (var i = 0; i < queries.Count; i++)
        {
            var recall = recallTasks[i].Result;

            // Hot index: take from first result (identical across queries for same agent)
            if (i == 0)
                aggregatedRecall.HotIndex = recall.HotIndex;

            // Warm memories: deduplicate by ID
            foreach (var warm in recall.WarmMemories)
            {
                if (seenWarmIds.Add(warm.Id))
                    aggregatedRecall.WarmMemories.Add(warm);
            }

            // Freshness warnings: collect unique
            foreach (var warning in recall.FreshnessWarnings)
            {
                if (!aggregatedRecall.FreshnessWarnings.Contains(warning))
                    aggregatedRecall.FreshnessWarnings.Add(warning);
            }

            // Archive results: deduplicate by ID, keep lowest distance
            var archive = archiveTasks[i].Result;
            foreach (var result in archive)
            {
                if (!allArchiveResults.TryGetValue(result.Entry.Id, out var existing) ||
                    result.Distance < existing.Distance)
                {
                    allArchiveResults[result.Entry.Id] = result;
                }
            }
        }

        var archiveResults = allArchiveResults.Values
            .OrderBy(r => r.Distance)
            .ToList();

        var uniqueCount = seenWarmIds.Count + allArchiveResults.Keys.Except(seenWarmIds).Count();

        return new SyntheticImaginingResult
        {
            AggregatedRecall = aggregatedRecall,
            ArchiveResults = archiveResults,
            UniqueMemoryCount = uniqueCount,
            Success = true
        };
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private async Task<IChatClient?> GetChatClientAsync()
    {
        try
        {
            var chatClientService = _serviceProvider.GetService<IFabrCoreChatClientService>();
            if (chatClientService is null)
                return null;

            var modelName = _options.Models.ResolveModelForCall(LlmModelTier.Small, _options.Models.ImaginingModelName);
            return await chatClientService.GetChatClient(modelName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve chat client for imagining");
            return null;
        }
    }

    /// <summary>
    /// Remove memory-context marker blocks from message text so previously recalled
    /// memories don't pollute the conversation context sent for query generation.
    /// </summary>
    private static string StripMemoryContextMarkers(string text)
    {
        if (!text.Contains(AgentMemoryService.MemoryContextStart))
            return text;

        return MemoryContextPattern().Replace(text, "").Trim();
    }

    [GeneratedRegex(
        @"<memory-context source=""agent-memory-system"">.*?</memory-context>",
        RegexOptions.Singleline)]
    private static partial Regex MemoryContextPattern();
}
