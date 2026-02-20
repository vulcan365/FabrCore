using FabrCore.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

public record CompactionConfig
{
    public bool Enabled { get; init; } = true;
    public int KeepLastN { get; init; } = 20;
    public int? MaxContextTokens { get; init; }
    public double Threshold { get; init; } = 0.75;
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
    private readonly IFabrCoreChatClientService _chatClientService;
    private readonly ILogger<CompactionService> _logger;

    public CompactionService(IFabrCoreChatClientService chatClientService, ILogger<CompactionService> logger)
    {
        _chatClientService = chatClientService;
        _logger = logger;
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

        if (config.MaxContextTokens is null or <= 0)
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
        var threshold = (int)(config.MaxContextTokens.Value * config.Threshold);

        if (estimatedTokens <= threshold)
        {
            return new CompactionResult
            {
                WasCompacted = false,
                OriginalMessageCount = messages.Count,
                EstimatedTokensBefore = estimatedTokens
            };
        }

        _logger.LogInformation(
            "Compaction triggered: {Tokens} estimated tokens exceeds threshold {Threshold} ({Ratio:P0} of {Max})",
            estimatedTokens, threshold, config.Threshold, config.MaxContextTokens.Value);

        if (onCompacting is not null)
            await onCompacting();

        // Split: summarize older messages, keep recent ones
        var keepCount = Math.Min(config.KeepLastN, messages.Count);
        var splitIndex = messages.Count - keepCount;

        // If KeepLastN covers all messages but we're over threshold,
        // reduce the keep window so we actually compact something.
        // Always keep at least 2 messages (the most recent exchange).
        if (splitIndex == 0 && messages.Count > 2)
        {
            keepCount = Math.Max(2, messages.Count / 2);
            splitIndex = messages.Count - keepCount;
            _logger.LogInformation(
                "KeepLastN ({KeepLastN}) covers all {Count} messages — reducing keep window to {Keep} to force compaction",
                config.KeepLastN, messages.Count, keepCount);
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

        var summary = await SummarizeAsync(toSummarize, modelConfigName, ct);

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

    public static int EstimateTokens(List<StoredChatMessage> messages)
    {
        var totalChars = 0;
        foreach (var m in messages)
        {
            totalChars += m.ContentsJson?.Length ?? 0;
            totalChars += m.Role?.Length ?? 0;
            totalChars += m.AuthorName?.Length ?? 0;
        }
        // chars / 4 heuristic
        return totalChars / 4;
    }

    private async Task<string> SummarizeAsync(
        List<StoredChatMessage> messages,
        string modelConfigName,
        CancellationToken ct)
    {
        var chatClient = await _chatClientService.GetChatClient(modelConfigName);

        var formattedMessages = string.Join("\n", messages.Select(m =>
        {
            var content = m.ContentsJson ?? "";
            // Try to extract plain text from the JSON for readability
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
                // Fall back to raw JSON
            }
            return $"[{m.Role}] {content}";
        }));

        var prompt = $"""
            Summarize the following conversation history concisely. Preserve:
            - Key decisions and conclusions
            - Important facts, names, and numbers
            - Outstanding tasks or open questions
            - The overall topic and context

            Conversation:
            {formattedMessages}
            """;

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { MaxOutputTokens = 2048 },
            ct);

        return response.Text ?? "Unable to generate summary.";
    }
}
