namespace FabrCore.Services.Memory.Models;

/// <summary>
/// A planned sequence of retrieval operations. Produced by <see cref="Abstractions.IRetrievalPlanner"/>
/// and executed by the memory service. Replaces the hard-coded 3-stage pipeline with a query-aware
/// chain so trivial queries can exit after the hot-index check and complex queries can fan out
/// into archive + graph expansion only when warranted.
/// </summary>
public class RetrievalPlan
{
    /// <summary>
    /// Ordered sequence of steps to execute. An empty plan is treated as <see cref="RetrievalStep.HotIndexOnly"/>.
    /// </summary>
    public List<RetrievalStep> Steps { get; set; } = [];

    /// <summary>
    /// Memory types the planner wants the retriever to prioritize for this query. Null = no filter.
    /// Used by the retriever as a soft bias, not a hard filter.
    /// </summary>
    public HashSet<MemoryType>? PreferredTypes { get; set; }

    /// <summary>
    /// Human-readable explanation of why the planner chose this plan. Recorded for telemetry and
    /// debugging — never shown to the agent LLM.
    /// </summary>
    public string? Rationale { get; set; }

    /// <summary>
    /// Whether the planner chose this plan via heuristic (cheap, deterministic) or LLM (more expensive).
    /// Useful for telemetry: high heuristic-hit ratio = planner is working as intended.
    /// </summary>
    public RetrievalPlanSource Source { get; set; } = RetrievalPlanSource.Heuristic;

    /// <summary>Convenience factory: the minimum-cost plan.</summary>
    public static RetrievalPlan HotIndexOnly(string? rationale = null) => new()
    {
        Steps = [RetrievalStep.HotIndexOnly],
        Rationale = rationale
    };

    /// <summary>Convenience factory: the legacy 3-stage + graph pipeline.</summary>
    public static RetrievalPlan Standard(string? rationale = null) => new()
    {
        Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand],
        Rationale = rationale
    };

    /// <summary>Convenience factory: full recall including cold archive.</summary>
    public static RetrievalPlan Deep(string? rationale = null) => new()
    {
        Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand, RetrievalStep.ArchiveSearch],
        Rationale = rationale
    };
}

/// <summary>
/// A single retrieval operation the planner can include in a <see cref="RetrievalPlan"/>.
/// Steps are composable — the executor runs them in order, accumulating results.
/// </summary>
public enum RetrievalStep
{
    /// <summary>
    /// Return only the hot layer index. Zero LLM cost, zero vector search.
    /// Appropriate when the query is trivial, the hot index likely already covers it, or the agent
    /// just needs its standing context refreshed.
    /// </summary>
    HotIndexOnly,

    /// <summary>
    /// Scan memory headers and use an LLM to select relevant ones, then load their full content.
    /// The baseline retrieval path — one LLM call for selection, N DB reads for the selected memories.
    /// </summary>
    HeaderScanLlmSelect,

    /// <summary>
    /// Skip the LLM selection call and rank candidates by embedding distance only.
    /// Cheaper than <see cref="HeaderScanLlmSelect"/>, appropriate for queries where semantic
    /// similarity is a strong enough signal (e.g., lookups by specific entity name).
    /// </summary>
    VectorOnly,

    /// <summary>
    /// Traverse graph relationships outward from the currently-selected memories and include their
    /// neighbors. Must run after a step that produced seed memories.
    /// </summary>
    GraphExpand,

    /// <summary>
    /// Vector-search the cold layer archive as well. Used when the hot/warm layers are unlikely
    /// to contain the answer and the agent needs to reach back into older memories.
    /// </summary>
    ArchiveSearch,

    /// <summary>
    /// Query the hierarchical semantic summary tree for topic-level nodes that cover the query.
    /// Produces <c>MemorySummaryNode</c> rollups instead of individual memories — dramatically
    /// cheaper than an LLM selection pass over the full header list when the query is broad.
    /// No-op when <see cref="Configuration.AgentMemoryOptions.SummaryTreeEnabled"/> is false.
    /// </summary>
    SummaryTreeScan
}

/// <summary>How the planner produced the plan. Primarily for telemetry.</summary>
public enum RetrievalPlanSource
{
    /// <summary>Deterministic rules (query length, hot-index overlap, temporal markers, etc.).</summary>
    Heuristic,

    /// <summary>An LLM chose the plan by classifying query complexity.</summary>
    Llm,

    /// <summary>The planner was disabled; the plan is the legacy Standard plan.</summary>
    Disabled
}
