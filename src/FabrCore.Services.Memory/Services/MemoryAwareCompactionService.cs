using System.Text.Json;
using FabrCore.Core;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Multi-tier compaction service that integrates memory extraction into the compaction pipeline.
/// Tier 1: Tool result compression (free, no LLM)
/// Tier 2: Memory extraction (LLM, produces durable graph artifacts)
/// Tier 3: Structured summarization (LLM, last resort)
/// Post-compaction: hot memory index re-injection
/// </summary>
public class MemoryAwareCompactionService
{
    private readonly IFabrCoreChatClientService? _chatClientService;
    private readonly ILogger<MemoryAwareCompactionService> _logger;

    public MemoryAwareCompactionService(
        ILoggerFactory loggerFactory,
        IFabrCoreChatClientService? chatClientService = null)
    {
        _chatClientService = chatClientService;
        _logger = loggerFactory.CreateLogger<MemoryAwareCompactionService>();
    }

    /// <summary>
    /// Run the full tier cascade: compress tool results → extract memories → structured summarization.
    /// Each tier checks whether the token threshold is satisfied before proceeding to the next.
    /// </summary>
    public async Task<CompactionResult> CompactAsync(
        FabrCoreChatHistoryProvider provider,
        CompactionConfig config,
        IAgentMemoryService memoryService,
        AgentMemoryOptions memoryOptions,
        string modelConfigName,
        CancellationToken ct = default)
    {
        if (!config.Enabled || config.MaxContextTokens <= 0)
            return new CompactionResult { WasCompacted = false };

        // Flush pending messages
        if (provider.HasPendingMessages)
            await provider.FlushAsync(ct);

        var messages = await provider.GetStoredMessagesAsync();
        var tokensBefore = CompactionService.EstimateTokens(messages);
        var threshold = (int)(config.MaxContextTokens * config.Threshold);

        if (tokensBefore <= threshold)
        {
            _logger.LogDebug("Compaction not needed: {Tokens} tokens <= {Threshold} threshold",
                tokensBefore, threshold);
            return new CompactionResult
            {
                WasCompacted = false,
                OriginalMessageCount = messages.Count,
                EstimatedTokensBefore = tokensBefore
            };
        }

        _logger.LogInformation(
            "Compaction triggered: {Tokens} tokens exceeds threshold {Threshold} ({Ratio:P0} of {Max})",
            tokensBefore, threshold, config.Threshold, config.MaxContextTokens);

        var originalCount = messages.Count;
        var memoriesExtracted = 0;
        var toolResultsCompressed = 0;

        // ─── Tier 1: Tool result compression (free, no LLM) ────────

        var (compressedMessages, compressed) = ToolResultCompressor.CompressToolResults(
            messages,
            config.KeepLastN,
            memoryOptions.Compaction.ToolResultCompressionThreshold,
            memoryOptions.Compaction.ToolResultKeepHeadChars,
            memoryOptions.Compaction.ToolResultKeepTailChars);

        if (compressed > 0)
        {
            toolResultsCompressed = compressed;
            messages = compressedMessages;
            var tokensAfterTier1 = CompactionService.EstimateTokens(messages);

            _logger.LogInformation("Tier 1: compressed {Count} tool results, ~{Before} -> ~{After} tokens",
                compressed, tokensBefore, tokensAfterTier1);

            if (tokensAfterTier1 <= threshold)
            {
                _logger.LogInformation("Tier 1 sufficient — skipping Tiers 2 and 3");
                await provider.ReplaceAndResetCacheAsync(messages);
                return new CompactionResult
                {
                    WasCompacted = true,
                    OriginalMessageCount = originalCount,
                    CompactedMessageCount = messages.Count,
                    EstimatedTokensBefore = tokensBefore,
                    EstimatedTokensAfter = tokensAfterTier1
                };
            }
        }

        // ─── Tier 2: Memory extraction (LLM, produces durable artifacts) ────

        var keepCount = Math.Min(config.KeepLastN, messages.Count);
        var splitIndex = messages.Count - keepCount;

        // Adjust split past orphaned tool messages
        while (splitIndex < messages.Count &&
               string.Equals(messages[splitIndex].Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            splitIndex++;
        }

        if (splitIndex > 0)
        {
            var olderMessages = ConvertToChatMessages(messages.Take(splitIndex).ToList());
            if (olderMessages.Count > 0)
            {
                try
                {
                    var extracted = await memoryService.ExtractMemoriesAsync(olderMessages, ct);
                    memoriesExtracted = extracted.Count;
                    _logger.LogInformation("Tier 2: extracted {Count} durable memories from {Messages} older messages",
                        extracted.Count, olderMessages.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Tier 2: memory extraction failed, continuing with Tier 3");
                }
            }
        }

        // ─── Tier 3: Structured summarization (LLM, last resort) ────

        var toSummarize = messages.Take(splitIndex).ToList();
        var toKeep = messages.Skip(splitIndex).ToList();

        if (toSummarize.Count == 0)
        {
            // Edge case: all messages are in the keep window but still over threshold
            // Force at least 50% to be summarized
            if (messages.Count > 2)
            {
                keepCount = Math.Max(2, messages.Count / 2);
                splitIndex = messages.Count - keepCount;

                // Re-adjust past tool messages
                while (splitIndex < messages.Count &&
                       string.Equals(messages[splitIndex].Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    splitIndex++;
                }

                toSummarize = messages.Take(splitIndex).ToList();
                toKeep = messages.Skip(splitIndex).ToList();
            }
        }

        if (toSummarize.Count == 0)
        {
            _logger.LogInformation("Nothing to summarize after tier cascade");
            return new CompactionResult
            {
                WasCompacted = false,
                OriginalMessageCount = originalCount,
                EstimatedTokensBefore = tokensBefore
            };
        }

        _logger.LogInformation("Tier 3: summarizing {Count} messages, keeping {Keep} recent messages",
            toSummarize.Count, toKeep.Count);

        var summary = await SummarizeAsync(toSummarize, memoriesExtracted, modelConfigName, memoryOptions, ct);

        // ─── Post-compaction rebuild ────────────────────────────────

        var newMessages = new List<StoredChatMessage>();

        // Summary message
        newMessages.Add(new StoredChatMessage
        {
            Role = "system",
            AuthorName = "compaction",
            Timestamp = DateTime.UtcNow,
            ContentsJson = SerializeTextContent($"[Compacted History]\n{summary}")
        });

        // Re-inject hot memory index
        if (memoryOptions.HotIndex.ReInjectAfterCompaction)
        {
            var hotIndex = await memoryService.GetMemoryIndexAsync(ct);
            if (hotIndex.Entries.Count > 0)
            {
                var indexText = "Agent memory index (recalled from memory store):\n" +
                    string.Join("\n", hotIndex.Entries.Select(e =>
                        $"- [{e.Type}] {e.Title}: {e.DescriptionHook}"));

                newMessages.Add(new StoredChatMessage
                {
                    Role = "system",
                    AuthorName = "agent-memory",
                    Timestamp = DateTime.UtcNow,
                    ContentsJson = SerializeTextContent(indexText)
                });

                _logger.LogDebug("Re-injected hot memory index ({Count} entries) after compaction",
                    hotIndex.Entries.Count);
            }
        }

        // Kept recent messages
        newMessages.AddRange(toKeep);

        await provider.ReplaceAndResetCacheAsync(newMessages);

        var tokensAfter = CompactionService.EstimateTokens(newMessages);

        _logger.LogInformation(
            "Compaction complete: {Before}->{After} messages, ~{TokensBefore}->~{TokensAfter} tokens, " +
            "{ToolsCompressed} tool results compressed, {MemoriesExtracted} memories extracted",
            originalCount, newMessages.Count, tokensBefore, tokensAfter,
            toolResultsCompressed, memoriesExtracted);

        return new CompactionResult
        {
            WasCompacted = true,
            OriginalMessageCount = originalCount,
            CompactedMessageCount = newMessages.Count,
            EstimatedTokensBefore = tokensBefore,
            EstimatedTokensAfter = tokensAfter
        };
    }

    private async Task<string> SummarizeAsync(
        List<StoredChatMessage> messages,
        int memoriesExtracted,
        string modelConfigName,
        AgentMemoryOptions options,
        CancellationToken ct)
    {
        if (_chatClientService is null)
        {
            _logger.LogWarning("No IFabrCoreChatClientService available — Tier 3 summarization skipped");
            return "Compaction summary unavailable (no chat client configured).";
        }

        var chatClient = await _chatClientService.GetChatClient(modelConfigName);

        var formattedMessages = FormatMessagesForSummary(messages);

        var memoryNote = memoriesExtracted > 0
            ? $"\n\nIMPORTANT: {memoriesExtracted} durable memories were extracted and saved to the agent's memory store before this compaction. The agent can retrieve them via its recall tool. The summary does not need to carry those details — focus on transient state that didn't qualify as durable memory."
            : "";

        var systemPrompt = $"""
            You are producing a continuity-preserving handover summary for an ongoing agent task.
            Goal: enable the agent to continue seamlessly without access to the full transcript.

            Analyze the conversation and produce exactly these sections:

            ## Active Intent
            What the user is currently trying to accomplish. Include acceptance criteria if stated.

            ## Key Decisions
            Decisions made, constraints established, preferences confirmed. Include rationale when given.

            ## Current State
            What was last attempted. What worked. What failed and why.

            ## Open Items
            Outstanding tasks, unresolved questions, blocked items.

            ## Critical References
            Specific identifiers, names, system details, domain terms that must survive.
            Preserve these exactly — do not paraphrase technical identifiers.
            {memoryNote}

            Optimize for the agent's ability to continue working, not human readability.
            Be precise. Preserve exact identifiers. Omit pleasantries and redundant context.
            """;

        var userPrompt = $"""
            Produce the handover summary for this conversation:

            {formattedMessages}
            """;

        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, userPrompt)
            ],
            new ChatOptions { MaxOutputTokens = options.Compaction.SummaryMaxTokens },
            ct);

        return response.Text ?? "Unable to generate summary.";
    }

    private static string FormatMessagesForSummary(List<StoredChatMessage> messages)
    {
        return string.Join("\n", messages.Select(m =>
        {
            var content = m.ContentsJson ?? "";
            try
            {
                var contents = JsonSerializer.Deserialize<List<AIContent>>(
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
    }

    private static List<ChatMessage> ConvertToChatMessages(List<StoredChatMessage> stored)
    {
        var result = new List<ChatMessage>();
        foreach (var msg in stored)
        {
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue; // Tool results don't carry user/assistant conversation content

            var role = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.User
                : ChatRole.Assistant;

            var text = ExtractText(msg);
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new ChatMessage(role, text));
        }
        return result;
    }

    private static string? ExtractText(StoredChatMessage msg)
    {
        if (string.IsNullOrEmpty(msg.ContentsJson))
            return null;

        try
        {
            var contents = JsonSerializer.Deserialize<List<AIContent>>(
                msg.ContentsJson, Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions);
            var text = string.Join(" ", contents?
                .OfType<TextContent>()
                .Select(tc => tc.Text) ?? []);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeTextContent(string text)
    {
        return JsonSerializer.Serialize(
            new List<AIContent> { new TextContent(text) },
            Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions);
    }
}
