#pragma warning disable MAAI001 // Microsoft.Agents.AI.Compaction is for evaluation purposes only and may change.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk;

/// <summary>Per-call, reversible tool-output compaction settings.</summary>
/// <remarks>Task-bearing messages remain intact. Working-set targets are soft; request safety enforces capacity.</remarks>
public record ContextCompactionConfig
{
    /// <summary>Whether context compaction is composed at all. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The model's total context window. Sourced from <c>ModelConfiguration.ContextWindowTokens</c>.
    /// Zero means unknown, which disables context compaction entirely.
    /// </summary>
    public int MaxContextWindowTokens { get; init; }

    /// <summary>
    /// The model's maximum output tokens. Sourced from <c>ModelConfiguration.MaxOutputTokens</c>.
    /// Zero means unknown, which disables context compaction entirely.
    /// </summary>
    public int MaxOutputTokens { get; init; }

    /// <summary>Optional conversation-input working set, independent of the physical model window.</summary>
    public int? WorkingSetTokens { get; init; }

    /// <summary>
    /// Fraction of the input budget at which older tool results receive bounded excerpts.
    /// This is the cheapest rung — free, reversible, and it degrades rather than deletes.
    /// </summary>
    public double EvictThreshold { get; init; } = ContextCompaction.DefaultEvictThreshold;

    /// <summary>
    /// Fraction of the input budget at which older tool excerpts are tightened further.
    /// Must be greater than or equal to <see cref="EvictThreshold"/>.
    /// </summary>
    public double TruncateThreshold { get; init; } = ContextCompaction.DefaultTruncateThreshold;

    /// <summary>Tokens available for conversation input: window minus reserved output, capped by the optional working set.</summary>
    public int InputBudgetTokens => Math.Min(
        Math.Max(0, MaxContextWindowTokens - MaxOutputTokens),
        WorkingSetTokens ?? int.MaxValue);

    /// <summary>Absolute token count at which tool-result eviction fires.</summary>
    public int EvictAtTokens => (int)(InputBudgetTokens * EvictThreshold);

    /// <summary>Absolute token count at which tighter tool excerpts are applied.</summary>
    public int TruncateAtTokens => (int)(InputBudgetTokens * TruncateThreshold);

    /// <summary>
    /// True when this config can actually produce a strategy. False when the model configuration is
    /// missing the window or output-token values, or the thresholds are out of order — in which case the
    /// agent runs with no in-run context bound and only layers 2–4 protect it.
    /// </summary>
    public bool IsUsable =>
        Enabled
        && MaxContextWindowTokens > 0
        && MaxOutputTokens > 0
        && MaxOutputTokens < MaxContextWindowTokens
        && (WorkingSetTokens is null or > 0)
        && EvictThreshold is > 0.0 and <= 1.0
        && TruncateThreshold is > 0.0 and <= 1.0
        && TruncateThreshold >= EvictThreshold;
}

/// <summary>
/// Builds the layer 1 <see cref="CompactionProvider"/> and keeps its state out of durable storage.
/// </summary>
public static class ContextCompaction
{
    /// <summary>Default fraction of the input budget at which tool-result eviction fires.</summary>
    public const double DefaultEvictThreshold = 0.5;

    /// <summary>Default fraction of the input budget at which truncation fires.</summary>
    public const double DefaultTruncateThreshold = 0.8;

    /// <summary>
    /// The <c>AgentSession.StateBag</c> key the context-compaction group index is stored under.
    /// </summary>
    /// <remarks>
    /// Pinned to an explicit value rather than the framework default (the strategy's type name) so
    /// <see cref="StripSessionState"/> can find and remove it regardless of which strategy is composed.
    /// </remarks>
    public const string StateKey = "_fabrcore_context_compaction";

    /// <summary>
    /// Creates the context-compaction provider, or <see langword="null"/> when
    /// <paramref name="config"/> is not usable.
    /// </summary>
    /// <remarks>
    /// Uses the framework group index with FabrCore's protected tool strategy. Older tool output is
    /// excerpted at the two thresholds; user messages, instructions, assistant prose, handovers, tool
    /// arguments and the latest two groups remain intact. This layer makes no summarization calls.
    /// </remarks>
    /// <remarks>
    /// Returns the base <see cref="Microsoft.Agents.AI.AIContextProvider"/> rather than the concrete
    /// <see cref="CompactionProvider"/> deliberately: the compaction namespace is <c>[Experimental]</c>
    /// and churns across 1.x, so the experimental surface stays contained in this one file.
    /// </remarks>
    public static Microsoft.Agents.AI.AIContextProvider? TryCreateProvider(
        ContextCompactionConfig config,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.IsUsable)
        {
            return null;
        }

        var strategy = new ProtectedToolCompactionStrategy(config);

        return new CompactionProvider(strategy, StateKey, loggerFactory);
    }

    // Working-set targets are soft. Dropping user requests, constraints, summaries or
    // assistant decisions to meet them is unsafe. The final request guard enforces capacity.
    internal sealed class ProtectedToolCompactionStrategy(ContextCompactionConfig config)
        : CompactionStrategy(_ => true)
    {
        protected override ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index,
            ILogger logger, CancellationToken cancellationToken)
        {
            var changed = CompactTools(2048, config.EvictAtTokens);
            changed |= CompactTools(512, config.TruncateAtTokens);
            return ValueTask.FromResult(changed);

            bool CompactTools(int maxChars, int target)
            {
                var changedHere = false;
                var included = index.Groups.Where(g => !g.IsExcluded).ToList();
                var recent = included.Where(g => g.Kind != CompactionGroupKind.System).TakeLast(2).ToHashSet();
                foreach (var group in included)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ChatRunSafetyScope.EstimateTokens(index.GetIncludedMessages()) <= target) break;
                    if (recent.Contains(group) || group.Kind != CompactionGroupKind.ToolCall) continue;
                    var calls = group.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();
                    var results = group.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId).ToHashSet();
                    if (!calls.SetEquals(results)) continue;
                    var changedGroup = false;
                    List<ChatMessage> replacement = [];
                    foreach (var message in group.Messages)
                    {
                        List<AIContent> contents = [];
                        foreach (var content in message.Contents)
                        {
                            if (content is FunctionResultContent result)
                            {
                                var text = CompactionTranscript.ResultText(result.Result);
                                if (text.Length > maxChars)
                                {
                                    contents.Add(new FunctionResultContent(result.CallId, CompactionTranscript.Bound(text, maxChars)));
                                    changedGroup = true;
                                    continue;
                                }
                            }
                            contents.Add(content);
                        }
                        replacement.Add(new ChatMessage(message.Role, contents)
                        { AuthorName = message.AuthorName, MessageId = message.MessageId, AdditionalProperties = message.AdditionalProperties });
                    }
                    if (!changedGroup) continue;
                    group.IsExcluded = true;
                    group.ExcludeReason = "Tool output excerpt; original remains in history.";
                    index.InsertGroup(index.Groups.IndexOf(group) + 1, group.Kind, replacement, group.TurnIndex);
                    changedHere = true;
                }
                return changedHere;
            }
        }
    }

    /// <summary>
    /// Removes the context-compaction entry from a serialized session before it is persisted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The group index holds a full copy of every message it has seen. Persisting it would do two bad
    /// things: duplicate the whole conversation into the agent's state blob — which is rewritten in full
    /// on every write — and let a stale index outlive a layer 2 rewrite of the thread, re-sending messages
    /// that history compaction had already summarized away.
    /// </para>
    /// <para>
    /// Dropping it is free. The protected tool strategy is deterministic and makes no
    /// LLM calls, so the index rebuilds itself from the message list on the next activation at no cost.
    /// </para>
    /// </remarks>
    /// <param name="payload">The serialized session produced by <c>AIAgent.SerializeSessionAsync</c>.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>The payload without the context-compaction state, or the original payload when it was absent.</returns>
    public static JsonElement StripSessionState(JsonElement payload, ILogger? logger = null)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return payload;
        }

        try
        {
            if (JsonNode.Parse(payload.GetRawText()) is not JsonObject root
                || root["stateBag"] is not JsonObject stateBag
                || !stateBag.Remove(StateKey))
            {
                return payload;
            }

            using var document = JsonDocument.Parse(root.ToJsonString());
            logger?.LogDebug("Stripped context-compaction state from harness session snapshot");
            return document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            // Persisting a slightly-too-large snapshot is survivable; failing the turn over it is not.
            logger?.LogWarning(ex, "Could not strip context-compaction state from the session snapshot — persisting it as-is");
            return payload;
        }
    }
}
