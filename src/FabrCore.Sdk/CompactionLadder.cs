using System.Text;

namespace FabrCore.Sdk;

/// <summary>Resolved thresholds for request compaction, durable summarization and safety checks.</summary>
/// <remarks>
/// Tool excerpt thresholds are soft targets applied inside a run. History summarization runs between
/// turns and may trigger earlier. Projection and request guards stop when protected context cannot fit.
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

    /// <summary>The token count at which projection must fit or stop, or 0 when disabled.</summary>
    public int FuseAtTokens =>
        Projection.Enabled && Projection.MaxContextTokens > 0
            ? (int)(Projection.MaxContextTokens * Projection.Threshold)
            : 0;

    /// <summary>
    /// True when projection or run safety would stop below the configured durable history threshold.
    /// History may legitimately summarize before either soft tool-excerpt threshold.
    /// </summary>
    public bool IsOutOfOrder
    {
        get
        {
            var rungs = new[] { HistoryAtTokens, FuseAtTokens, RunSafety.MaxPromptInputTokens }
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
    /// Renders the configured thresholds. This is not an execution sequence: history runs between turns.
    /// Disabled layers are shown explicitly.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>(5);

        if (Context.IsUsable)
        {
            parts.Add($"tool-excerpt@{Context.EvictAtTokens}");
            parts.Add($"tool-excerpt-tight@{Context.TruncateAtTokens}");
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
