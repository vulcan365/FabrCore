using System.Text;

namespace FabrCore.Sdk;

/// <summary>
/// The resolved compaction ladder for one agent + model configuration.
/// </summary>
/// <remarks>
/// <para>
/// FabrCore bounds context with five ordered rungs, cheapest and most reversible first:
/// </para>
/// <list type="number">
/// <item><description><b>evict</b> — layer 1, old tool results collapse to one-line summaries. Free, reversible.</description></item>
/// <item><description><b>truncate</b> — layer 1, oldest groups drop out of the request. Free, reversible.</description></item>
/// <item><description><b>history</b> — layer 2, thread is summarized and rewritten. One LLM call, permanent.</description></item>
/// <item><description><b>fuse</b> — read-side projection. Blunt clip, insurance only.</description></item>
/// <item><description><b>stop</b> — run safety. <see cref="FabrCoreRunStoppedException"/>, nothing survives.</description></item>
/// </list>
/// <para>
/// Rungs 1–2 are <see cref="ContextCompaction"/>, rung 3 is <see cref="CompactionService"/>, rung 4 is
/// <see cref="FabrCoreChatHistoryProvider.ActiveProjection"/>, rung 5 is <see cref="ChatRunSafetyScope"/>.
/// Everything is anchored to one setting — <c>ModelConfiguration.ContextWindowTokens</c> — so the rungs
/// stay in order without anyone tuning them individually.
/// </para>
/// </remarks>
public sealed record CompactionLadder
{
    /// <summary>Layer 1 — in-run context compaction.</summary>
    public required ContextCompactionConfig Context { get; init; }

    /// <summary>Layer 2 — history compaction against the persisted thread.</summary>
    public required CompactionConfig History { get; init; }

    /// <summary>The read-side projection fuse.</summary>
    public required ProjectionConfig Projection { get; init; }

    /// <summary>The run-safety budget stop.</summary>
    public required ChatRunSafetyConfig RunSafety { get; init; }

    /// <summary>The token count at which history compaction fires, or 0 when disabled.</summary>
    public int HistoryAtTokens =>
        History.Enabled && History.MaxContextTokens > 0
            ? (int)(History.MaxContextTokens * History.Threshold)
            : 0;

    /// <summary>The token count at which the projection fuse clips, or 0 when disabled.</summary>
    public int FuseAtTokens =>
        Projection.Enabled && Projection.MaxContextTokens > 0
            ? (int)(Projection.MaxContextTokens * Projection.Threshold)
            : 0;

    /// <summary>
    /// True when the ladder is out of order — a later rung would fire before an earlier one, making the
    /// earlier rung decorative. Worth logging: it is nearly always a misconfiguration.
    /// </summary>
    public bool IsOutOfOrder
    {
        get
        {
            var rungs = new[] { Context.TruncateAtTokens, HistoryAtTokens, FuseAtTokens }
                .Where(t => t > 0)
                .ToArray();

            for (var i = 1; i < rungs.Length; i++)
            {
                if (rungs[i] < rungs[i - 1])
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Renders the ladder as one readable line, e.g.
    /// <c>evict@92000 → truncate@147200 → history@174000 → fuse@180000 → stop@200000</c>.
    /// Disabled rungs render as <c>name:off</c> so a missing bound is visible rather than implied.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>(5);

        if (Context.IsUsable)
        {
            parts.Add($"evict@{Context.EvictAtTokens}");
            parts.Add($"truncate@{Context.TruncateAtTokens}");
        }
        else
        {
            parts.Add(Context.Enabled ? "context:unconfigured" : "context:off");
        }

        parts.Add(HistoryAtTokens > 0 ? $"history@{HistoryAtTokens}" : "history:off");
        parts.Add(FuseAtTokens > 0 ? $"fuse@{FuseAtTokens}" : "fuse:off");
        parts.Add(RunSafety.MaxPromptInputTokens > 0 ? $"stop@{RunSafety.MaxPromptInputTokens}" : "stop:off");

        var sb = new StringBuilder(string.Join(" → ", parts));

        if (RunSafety.PerTurnMaxInputTokens > 0)
        {
            sb.Append($" (turn budget {RunSafety.PerTurnMaxInputTokens})");
        }

        if (IsOutOfOrder)
        {
            sb.Append(" [OUT OF ORDER]");
        }

        return sb.ToString();
    }
}
