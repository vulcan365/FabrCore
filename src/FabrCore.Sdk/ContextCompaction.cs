#pragma warning disable MAAI001 // Microsoft.Agents.AI.Compaction is for evaluation purposes only and may change.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// Layer 1 of the FabrCore compaction ladder: <b>context compaction</b>.
/// </summary>
/// <remarks>
/// <para>
/// Context compaction bounds <i>what a single LLM call sees</i>. It runs before every model call inside
/// the tool loop, costs no LLM call, and is non-destructive — message groups are marked excluded, the
/// persisted thread in <c>MessageThreads</c> is never touched.
/// </para>
/// <para>
/// Layer 2 is <see cref="CompactionService"/> (<b>history compaction</b>), which bounds what is
/// <i>persisted</i> and does cost an LLM call. The two are ordered so the cheap reversible layer always
/// fires first — see <see cref="CompactionLadder"/>.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Fraction of the input budget at which old tool-call groups collapse into one-line summaries.
    /// This is the cheapest rung — free, reversible, and it degrades rather than deletes.
    /// </summary>
    public double EvictThreshold { get; init; } = ContextCompaction.DefaultEvictThreshold;

    /// <summary>
    /// Fraction of the input budget at which the oldest non-system groups are dropped from the request.
    /// Must be greater than or equal to <see cref="EvictThreshold"/>.
    /// </summary>
    public double TruncateThreshold { get; init; } = ContextCompaction.DefaultTruncateThreshold;

    /// <summary>Tokens available for conversation input: window minus reserved output.</summary>
    public int InputBudgetTokens => Math.Max(0, MaxContextWindowTokens - MaxOutputTokens);

    /// <summary>Absolute token count at which tool-result eviction fires.</summary>
    public int EvictAtTokens => (int)(InputBudgetTokens * EvictThreshold);

    /// <summary>Absolute token count at which truncation fires.</summary>
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
    /// The strategy is <see cref="ContextWindowCompactionStrategy"/>: evict old tool results at
    /// <see cref="ContextCompactionConfig.EvictThreshold"/>, then truncate the oldest groups at
    /// <see cref="ContextCompactionConfig.TruncateThreshold"/>. Summarization is deliberately not used
    /// here — an LLM call before every model call would add latency to the hot path, and its output would
    /// be discarded and re-billed on the next activation because this layer's state is not persisted.
    /// Summarization belongs to layer 2.
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

        var strategy = new ContextWindowCompactionStrategy(
            maxContextWindowTokens: config.MaxContextWindowTokens,
            maxOutputTokens: config.MaxOutputTokens,
            toolEvictionThreshold: config.EvictThreshold,
            truncationThreshold: config.TruncateThreshold);

        return new CompactionProvider(strategy, StateKey, loggerFactory);
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
    /// Dropping it is free. <see cref="ContextWindowCompactionStrategy"/> is deterministic and makes no
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
