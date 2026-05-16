using FabrCore.Core;
using FabrCore.Core.Monitoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;

namespace FabrCore.Sdk;

public record CompactionConfig
{
    public bool Enabled { get; init; } = true;
    public int KeepLastN { get; init; } = 20;
    public int MaxContextTokens { get; init; } = 25000;
    public double Threshold { get; init; } = 0.75;

    /// <summary>
    /// If the newest stored message is older than this many minutes AND stored tokens
    /// are over threshold, run compaction before the next OnMessage call ("preflight"
    /// compaction). This protects dormant threads that wake up with a large backlog
    /// from paying the full token cost on the first turn.
    /// Set to 0 or negative to disable preflight compaction entirely.
    /// Default: 60 minutes.
    /// </summary>
    public int StaleAfterMinutes { get; init; } = 60;
}

/// <summary>
/// Projection config controls the sliding window applied when the history provider
/// hands chat messages to the LLM. Storage is untouched — this only affects reads.
/// This is the safety net that bounds how many tokens any single LLM call can see,
/// regardless of how large the persisted thread has grown.
/// </summary>
public record ProjectionConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Hard ceiling on tokens visible to the LLM, in the same units as the compaction
    /// heuristic (<see cref="CompactionService.EstimateTokens"/>).
    /// </summary>
    public int MaxContextTokens { get; init; } = 25000;

    /// <summary>
    /// Fraction of <see cref="MaxContextTokens"/> to actually fill. Leaving headroom
    /// below the raw max accounts for the output tokens and system prompt not included
    /// in the stored history estimate.
    /// </summary>
    public double Threshold { get; init; } = 0.75;

    /// <summary>
    /// Always include at least this many of the most recent non-system messages even
    /// if the token ceiling would otherwise clip them. Prevents pathological
    /// single-message-over-budget cases from dropping the user's own turn.
    /// </summary>
    public int MinKeepLastN { get; init; } = 2;
}

public record CompactionResult
{
    public bool WasCompacted { get; init; }
    public int OriginalMessageCount { get; init; }
    public int CompactedMessageCount { get; init; }
    public int EstimatedTokensBefore { get; init; }
    public int EstimatedTokensAfter { get; init; }
}

public class CompactionService
{
    private const int MaxSummarizationInputChars = 200_000;
    private const int ChunkSummaryMaxOutputTokens = 1536;
    private const int FinalSummaryMaxOutputTokens = 2048;

    private readonly IFabrCoreChatClientService _chatClientService;
    private readonly ILogger<CompactionService> _logger;
    private readonly IAgentMessageMonitor? _monitor;

    public CompactionService(
        IFabrCoreChatClientService chatClientService,
        ILogger<CompactionService> logger,
        IAgentMessageMonitor? monitor = null)
    {
        _chatClientService = chatClientService;
        _logger = logger;
        _monitor = monitor;
    }

    public async Task<CompactionResult> CompactIfNeededAsync(
        FabrCoreChatHistoryProvider provider,
        CompactionConfig config,
        string modelConfigName,
        Func<Task>? onCompacting = null,
        CancellationToken ct = default)
    {
        if (!config.Enabled)
        {
            return new CompactionResult { WasCompacted = false };
        }

        if (config.MaxContextTokens <= 0)
        {
            _logger.LogDebug("Compaction enabled but MaxContextTokens not configured — skipping");
            return new CompactionResult { WasCompacted = false };
        }

        // Flush pending messages first so we get a complete picture
        if (provider.HasPendingMessages)
        {
            await provider.FlushAsync(ct);
        }

        var messages = await provider.GetStoredMessagesAsync();
        var estimatedTokens = EstimateTokens(messages);
        var threshold = (int)(config.MaxContextTokens * config.Threshold);

        _logger.LogDebug(
            "Compaction check: {MessageCount} messages, ~{EstimatedTokens} estimated tokens, threshold {Threshold} ({Ratio:P0} of {MaxContext})",
            messages.Count, estimatedTokens, threshold, config.Threshold, config.MaxContextTokens);

        if (estimatedTokens <= threshold)
        {
            _logger.LogDebug("Compaction not needed: {EstimatedTokens} tokens <= {Threshold} threshold", estimatedTokens, threshold);
            return new CompactionResult
            {
                WasCompacted = false,
                OriginalMessageCount = messages.Count,
                EstimatedTokensBefore = estimatedTokens
            };
        }

        _logger.LogInformation(
            "Compaction triggered: {Tokens} estimated tokens exceeds threshold {Threshold} ({Ratio:P0} of {Max})",
            estimatedTokens, threshold, config.Threshold, config.MaxContextTokens);

        if (onCompacting is not null)
            await onCompacting();

        // Budget-aware keep window: walk backward from newest, accumulating
        // tokens until we'd exceed what the model can hold after the summary.
        const int summaryReserve = 2500;
        var keepBudget = Math.Max(0, threshold - summaryReserve);

        var keepCount = 0;
        var keepTokens = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateTokens(messages[i]);
            if (keepTokens + msgTokens > keepBudget && keepCount >= 1)
                break;
            if (keepCount >= config.KeepLastN)
                break;
            keepTokens += msgTokens;
            keepCount++;
        }

        keepCount = Math.Max(1, keepCount);
        var splitIndex = messages.Count - keepCount;

        // If the budget walk kept everything, force at least one message
        // into the summarize window so compaction actually does something.
        if (splitIndex == 0 && messages.Count > 1)
        {
            splitIndex = 1;
            _logger.LogInformation(
                "Budget-aware keep window covers all {Count} messages — forcing oldest into summarization",
                messages.Count);
        }

        // Adjust split point forward past any orphaned "tool" role messages.
        // Tool messages must follow their assistant message with tool_calls —
        // if we split between them, the API rejects the orphaned tool result.
        while (splitIndex < messages.Count &&
               string.Equals(messages[splitIndex].Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            splitIndex++;
        }

        var toSummarize = messages.Take(splitIndex).ToList();
        var toKeep = messages.Skip(splitIndex).ToList();

        // Truncate oversized tool results in the kept window so the
        // post-compaction state actually fits under the threshold.
        if (toKeep.Count > 0)
        {
            var perMsgBudget = Math.Max(4000, keepBudget / Math.Max(1, toKeep.Count));
            toKeep = TruncateOversizedMessages(toKeep, perMsgBudget);
        }

        if (toSummarize.Count == 0)
        {
            _logger.LogInformation("Nothing to summarize — all messages are within keep window ({Count} messages)", messages.Count);
            return new CompactionResult
            {
                WasCompacted = false,
                OriginalMessageCount = messages.Count,
                EstimatedTokensBefore = estimatedTokens
            };
        }

        _logger.LogDebug("Summarizing {Count} older messages using model config '{ModelConfig}', keeping {KeepCount} recent messages",
            toSummarize.Count, modelConfigName, toKeep.Count);

        var summary = await SummarizeAsync(toSummarize, modelConfigName, ct);

        _logger.LogDebug("Summarization complete — summary length: {Length} chars", summary.Length);

        var summaryMessage = new StoredChatMessage
        {
            Role = "system",
            AuthorName = "compaction",
            Timestamp = DateTime.UtcNow,
            ContentsJson = System.Text.Json.JsonSerializer.Serialize(
                new List<AIContent> { new TextContent($"[Compacted History]\n{summary}") },
                Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions)
        };

        var newMessages = new List<StoredChatMessage> { summaryMessage };
        newMessages.AddRange(toKeep);

        await provider.ReplaceAndResetCacheAsync(newMessages);

        var tokensAfter = EstimateTokens(newMessages);

        // Post-compaction validation: if still over threshold, aggressively
        // truncate non-summary messages as a last resort.
        if (tokensAfter > threshold && newMessages.Count > 1)
        {
            _logger.LogWarning(
                "Post-compaction tokens ({TokensAfter}) still exceed threshold ({Threshold}) — applying aggressive truncation",
                tokensAfter, threshold);

            var summaryTokens = EstimateTokens(newMessages[0]);
            var remainingBudget = Math.Max(0, threshold - summaryTokens);
            var nonSummaryCount = newMessages.Count - 1;
            var aggressiveBudget = Math.Max(2000, remainingBudget / Math.Max(1, nonSummaryCount));

            var truncated = new List<StoredChatMessage> { newMessages[0] };
            truncated.AddRange(TruncateOversizedMessages(
                newMessages.Skip(1).ToList(), aggressiveBudget));

            await provider.ReplaceAndResetCacheAsync(truncated);
            newMessages = truncated;
            tokensAfter = EstimateTokens(newMessages);

            _logger.LogInformation(
                "Aggressive truncation complete: ~{TokensAfter} tokens after truncation",
                tokensAfter);
        }

        _logger.LogInformation(
            "Compaction complete: {Before} -> {After} messages, ~{TokensBefore} -> ~{TokensAfter} tokens",
            messages.Count, newMessages.Count, estimatedTokens, tokensAfter);

        return new CompactionResult
        {
            WasCompacted = true,
            OriginalMessageCount = messages.Count,
            CompactedMessageCount = newMessages.Count,
            EstimatedTokensBefore = estimatedTokens,
            EstimatedTokensAfter = tokensAfter
        };
    }

    public static int EstimateTokens(StoredChatMessage message)
    {
        var totalChars = 0;
        totalChars += message.ContentsJson?.Length ?? 0;
        totalChars += message.Role?.Length ?? 0;
        totalChars += message.AuthorName?.Length ?? 0;
        return totalChars / 4;
    }

    public static int EstimateTokens(List<StoredChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
            total += EstimateTokens(m);
        return total;
    }

    private async Task<string> SummarizeAsync(
        List<StoredChatMessage> messages,
        string modelConfigName,
        CancellationToken ct)
    {
        // Compaction calls typically run inside an OnMessage LlmUsageScope, so they'll
        // inherit that scope's agent handle and parent message correlation. When called
        // outside a scope, the monitor falls back to the constructor-captured handle.
        var chatClient = new TokenTrackingChatClient(
            await _chatClientService.GetChatClient(modelConfigName),
            agentHandle: LlmUsageScope.Current?.AgentHandle,
            monitor: _monitor,
            logger: _logger);

        // Tag the compaction LLM call with a "Compaction" origin so it can be distinguished
        // from the OnMessage LLM calls that happen around it.
        using var _compactionCtx = LlmCallContext.Begin(
            LlmUsageScope.Current?.AgentHandle ?? "",
            "Compaction",
            LlmUsageScope.Current?.TraceId);

        var formattedMessages = messages
            .Select(FormatStoredMessageForSummary)
            .ToList();

        var chunks = BuildSummaryChunks(formattedMessages, MaxSummarizationInputChars);
        if (chunks.Count > 1)
        {
            var totalChars = formattedMessages.Sum(m => m.Length);
            _logger.LogInformation(
                "Summarization input too large ({Chars} chars); summarizing in {ChunkCount} chunks of up to {Max} chars",
                totalChars, chunks.Count, MaxSummarizationInputChars);
        }

        var summaries = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var label = chunks.Count == 1
                ? "conversation history"
                : $"conversation history chunk {i + 1} of {chunks.Count}";

            summaries.Add(await SummarizeTextAsync(
                chatClient,
                chunks[i],
                label,
                ChunkSummaryMaxOutputTokens,
                ct));
        }

        if (summaries.Count == 1)
            return summaries[0];

        return await ReduceSummariesAsync(chatClient, summaries, ct);
    }

    private async Task<string> ReduceSummariesAsync(
        IChatClient chatClient,
        IReadOnlyList<string> summaries,
        CancellationToken ct)
    {
        var current = summaries.ToList();
        var pass = 1;

        while (current.Count > 1)
        {
            var summaryInputs = current
                .Select((summary, index) => $"Partial summary {index + 1}:\n{summary}")
                .ToList();
            var chunks = BuildSummaryChunks(summaryInputs, MaxSummarizationInputChars);

            if (chunks.Count == 1)
            {
                return await SummarizeTextAsync(
                    chatClient,
                    chunks[0],
                    "partial compaction summaries",
                    FinalSummaryMaxOutputTokens,
                    ct);
            }

            _logger.LogInformation(
                "Merging {SummaryCount} partial compaction summaries in {ChunkCount} chunks (pass {Pass})",
                current.Count, chunks.Count, pass);

            var next = new List<string>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                next.Add(await SummarizeTextAsync(
                    chatClient,
                    chunks[i],
                    $"partial compaction summary batch {i + 1} of {chunks.Count}",
                    ChunkSummaryMaxOutputTokens,
                    ct));
            }

            current = next;
            pass++;
        }

        return current.Count == 1 ? current[0] : "Unable to generate summary.";
    }

    private static List<string> BuildSummaryChunks(
        IReadOnlyList<string> entries,
        int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var entry in entries)
        {
            if (entry.Length > maxChars)
            {
                FlushCurrent();

                for (var offset = 0; offset < entry.Length; offset += maxChars)
                {
                    var length = Math.Min(maxChars, entry.Length - offset);
                    chunks.Add(entry.Substring(offset, length));
                }

                continue;
            }

            var separatorLength = current.Length == 0 ? 0 : 1;
            if (current.Length + separatorLength + entry.Length > maxChars)
            {
                FlushCurrent();
            }

            if (current.Length > 0)
                current.AppendLine();
            current.Append(entry);
        }

        FlushCurrent();
        return chunks;

        void FlushCurrent()
        {
            if (current.Length == 0)
                return;

            chunks.Add(current.ToString());
            current.Clear();
        }
    }

    private static string FormatStoredMessageForSummary(StoredChatMessage message)
    {
        var content = message.ContentsJson ?? "";

        // Try to extract plain text from the JSON for readability.
        try
        {
            var contents = System.Text.Json.JsonSerializer.Deserialize<List<AIContent>>(
                content, Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions);
            var text = string.Join(" ", contents?
                .OfType<TextContent>()
                .Select(tc => tc.Text) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                content = text;
        }
        catch
        {
            // Fall back to raw JSON.
        }

        return $"[{message.Role}] {content}";
    }

    private static async Task<string> SummarizeTextAsync(
        IChatClient chatClient,
        string text,
        string label,
        int maxOutputTokens,
        CancellationToken ct)
    {
        var prompt = $"""
            Summarize the following {label} concisely. Preserve:
            - Key decisions and conclusions
            - Important facts, names, and numbers
            - Outstanding tasks or open questions
            - The overall topic and context

            Conversation:
            {text}
            """;

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { MaxOutputTokens = maxOutputTokens },
            ct);

        return response.Text ?? "Unable to generate summary.";
    }

    internal static List<StoredChatMessage> TruncateOversizedMessages(
        List<StoredChatMessage> messages, int perMessageTokenBudget)
    {
        var result = new List<StoredChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (EstimateTokens(msg) > perMessageTokenBudget)
            {
                var truncated = TruncateSingleMessage(msg, perMessageTokenBudget);
                result.Add(truncated);
            }
            else
            {
                result.Add(msg);
            }
        }
        return result;
    }

    internal static StoredChatMessage TruncateSingleMessage(
        StoredChatMessage message, int tokenBudget)
    {
        if (message.ContentsJson is null)
            return message;

        List<AIContent>? contents;
        try
        {
            contents = System.Text.Json.JsonSerializer.Deserialize<List<AIContent>>(
                message.ContentsJson,
                Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions);
        }
        catch
        {
            return message;
        }

        if (contents is null || contents.Count == 0)
            return message;

        var changed = false;
        var charBudget = tokenBudget * 4;
        var perContentBudget = Math.Max(8000, charBudget / Math.Max(1, contents.Count));

        for (var i = 0; i < contents.Count; i++)
        {
            if (contents[i] is FunctionResultContent frc)
            {
                var resultStr = frc.Result?.ToString();
                if (resultStr is not null && resultStr.Length > perContentBudget)
                {
                    var truncatedResult = resultStr[..perContentBudget]
                        + $"\n\n[... truncated from ~{resultStr.Length / 4} estimated tokens during compaction]";
                    contents[i] = new FunctionResultContent(frc.CallId, truncatedResult);
                    changed = true;
                }
            }
            else if (contents[i] is TextContent tc)
            {
                if (tc.Text is not null && tc.Text.Length > perContentBudget)
                {
                    contents[i] = new TextContent(
                        tc.Text[..perContentBudget]
                        + "\n\n[... truncated during compaction]");
                    changed = true;
                }
            }
        }

        if (!changed)
            return message;

        var newJson = System.Text.Json.JsonSerializer.Serialize(
            contents,
            Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions);

        return new StoredChatMessage
        {
            Role = message.Role,
            AuthorName = message.AuthorName,
            Timestamp = message.Timestamp,
            ContentsJson = newJson
        };
    }
}
