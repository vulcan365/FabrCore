namespace FabrCore.Services.Memory.Models;

/// <summary>
/// The full result of a memory recall operation, containing both the hot layer index
/// and the selectively retrieved warm memories with freshness warnings.
/// </summary>
public class MemoryRecallResult
{
    /// <summary>The always-loaded hot layer memory index.</summary>
    public MemoryIndex HotIndex { get; set; } = new();

    /// <summary>Warm memories selected as relevant to the current query (up to WarmRetrievalLimit).</summary>
    public List<MemoryEntry> WarmMemories { get; set; } = [];

    /// <summary>
    /// Cold-archive search matches. Populated only when the retrieval plan includes an
    /// archive step (e.g., when the query contains temporal markers or the planner flagged
    /// the recall as deep). Ordered by ascending vector distance.
    /// </summary>
    public List<MemorySearchResult> ArchiveResults { get; set; } = [];

    /// <summary>
    /// Hierarchical summary-tree nodes matched for broad queries. Populated only when the
    /// retrieval plan includes a <see cref="RetrievalStep.SummaryTreeScan"/> step and
    /// <c>AgentMemoryOptions.SummaryTreeEnabled</c> is true.
    /// </summary>
    public List<MemorySummaryNode> SummaryNodes { get; set; } = [];

    /// <summary>Freshness warnings for memories older than the configured threshold.</summary>
    public List<string> FreshnessWarnings { get; set; } = [];

    /// <summary>
    /// The plan that produced this result. Null when recall was executed without a planner
    /// (e.g., <c>RetrievalPlannerEnabled = false</c>). Used for telemetry and debugging.
    /// </summary>
    public RetrievalPlan? Plan { get; set; }
}
