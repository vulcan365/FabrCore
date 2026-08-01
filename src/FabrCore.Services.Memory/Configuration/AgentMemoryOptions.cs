using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Configuration;

/// <summary>
/// Configuration options for the agent memory service.
/// </summary>
public class AgentMemoryOptions
{
    /// <summary>
    /// Name of the connection string in IConfiguration pointing to the SQL database
    /// hosting the <c>mem</c> schema. Set via <c>AddAgentMemoryServices(connectionStringName)</c>.
    /// </summary>
    public string ConnectionStringName { get; internal set; } = "";

    /// <summary>
    /// Dimension of the embedding vectors stored in <c>mem.MemoryChunk</c> and
    /// <c>mem.MemorySummaryNode</c>. Must match the configured embeddings model.
    /// Changing this after the schema has been created requires dropping the <c>mem</c>
    /// schema — the VECTOR column dimension is fixed at creation. Default: 1536.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>
    /// When true, missing connection string or unresolvable <c>IEmbeddings</c> at startup
    /// is logged as an error instead of failing the host. Intended for client-only hosts
    /// that register memory services but never save memories. Default: false (fail fast).
    /// </summary>
    public bool AllowStartupWithoutEmbeddings { get; set; }

    /// <summary>
    /// Allowed memory types. Defaults to all five types.
    /// Override to restrict what types agents can store.
    /// </summary>
    public HashSet<MemoryType> AllowedMemoryTypes { get; set; } =
        [MemoryType.Fact, MemoryType.Rule, MemoryType.Instruction, MemoryType.Observation, MemoryType.Procedural];

    /// <summary>
    /// When true, all memories extracted during compaction are marked as point-in-time snapshots.
    /// Point-in-time memories always receive a freshness warning during recall, regardless of age,
    /// and are pruned more aggressively (3 days vs 30 days).
    /// Use for agents whose extracted facts are database snapshots or transient query results.
    /// Default: false.
    /// </summary>
    public bool PointInTimeMemories { get; set; }

    /// <summary>Hot layer index bounds and behavior.</summary>
    public HotIndexOptions HotIndex { get; } = new();

    /// <summary>Recall/retrieval pipeline tuning.</summary>
    public RetrievalOptions Retrieval { get; } = new();

    /// <summary>Consolidation, dedup, and entity-matching thresholds.</summary>
    public ConsolidationOptions Consolidation { get; } = new();

    /// <summary>Hierarchical semantic summary tree configuration.</summary>
    public SummaryTreeOptions SummaryTree { get; } = new();

    /// <summary>Conversation compaction tiers (tool result compression, structured summary).</summary>
    public CompactionOptions Compaction { get; } = new();

    /// <summary>LLM model routing for memory operations.</summary>
    public MemoryModelOptions Models { get; } = new();
}

/// <summary>Hot layer index bounds and behavior.</summary>
public class HotIndexOptions
{
    /// <summary>Maximum number of entries in the hot layer index. Default: 20.</summary>
    public int MaxEntries { get; set; } = 20;

    /// <summary>Maximum estimated tokens for the hot layer index. Default: 3000.</summary>
    public int MaxTokens { get; set; } = 3000;

    /// <summary>
    /// Re-inject the hot memory index as a system message after compaction so the agent
    /// immediately knows what it remembers. Default: true.
    /// </summary>
    public bool ReInjectAfterCompaction { get; set; } = true;
}

/// <summary>Recall/retrieval pipeline tuning.</summary>
public class RetrievalOptions
{
    /// <summary>Maximum number of warm memories retrieved per query. Default: 5.</summary>
    public int WarmRetrievalLimit { get; set; } = 5;

    /// <summary>Maximum number of headers scanned during retrieval. Default: 200.</summary>
    public int HeaderScanLimit { get; set; } = 200;

    /// <summary>Memories older than this many days get a freshness warning. Default: 1.</summary>
    public int FreshnessDaysThreshold { get; set; } = 1;

    /// <summary>
    /// Maximum hops for graph traversal during recall.
    /// 0 = no graph traversal (vector search only), 1 = direct neighbors.
    /// Graph traversal runs in parallel with vector search — it doesn't add latency.
    /// Default: 1.
    /// </summary>
    public int RecallGraphHops { get; set; } = 1;

    /// <summary>
    /// When true, <c>RecallAsync</c> is routed through <see cref="Abstractions.IRetrievalPlanner"/>
    /// which picks a plan (hot-index-only, standard, or deep) per query instead of always running
    /// the 3-stage pipeline. Saves LLM cost on trivial queries and reaches further on temporal queries.
    /// Default: false (preserves existing behavior until users opt in).
    /// </summary>
    public bool PlannerEnabled { get; set; }

    /// <summary>
    /// Maximum number of search queries the imagining LLM can generate per invocation.
    /// Default: 5.
    /// </summary>
    public int MaxImaginingQueries { get; set; } = 5;
}

/// <summary>Consolidation, dedup, and entity-matching thresholds.</summary>
public class ConsolidationOptions
{
    /// <summary>Maximum total memory entities per scope. Default: 200.</summary>
    public int MemoryFileCap { get; set; } = 200;

    /// <summary>Whether to automatically consolidate when memory count exceeds MemoryFileCap. Default: false.</summary>
    public bool EnableAutoConsolidation { get; set; }

    /// <summary>
    /// Vector cosine distance threshold for duplicate detection.
    /// Pairs below this distance with the same MemoryType are considered duplicates.
    /// Default: 0.05 (very similar).
    /// </summary>
    public double DuplicateDistanceThreshold { get; set; } = 0.05;

    /// <summary>
    /// Vector distance threshold for entity matching on save.
    /// When saving a new memory, if an existing chunk is within this distance,
    /// the existing entity is updated (knowledge merged) instead of creating a duplicate.
    /// Default: 0.25 (moderate similarity). Lower = stricter matching.
    /// </summary>
    public double EntityMatchThreshold { get; set; } = 0.25;

    /// <summary>
    /// Whether to extract and create graph relationships between entities
    /// during memory extraction. Requires an LLM call per extraction batch.
    /// Default: true.
    /// </summary>
    public bool EnableRelationshipExtraction { get; set; } = true;
}

/// <summary>Hierarchical semantic summary tree configuration.</summary>
public class SummaryTreeOptions
{
    /// <summary>
    /// When true, consolidation builds a hierarchical semantic summary tree (<c>mem.MemorySummaryNode</c>)
    /// that the retriever can consult for broad topic-level queries, reducing LLM selection cost and
    /// context size vs. scanning every header. Default: false (opt-in).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum depth of the summary tree. Level 0 is the root rollup; higher depths mean finer
    /// topic splits. The baseline builder currently materializes only depth 0; reserved for
    /// future multi-level builders. Default: 2.
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>
    /// Maximum number of child memories the builder samples when producing each node's summary.
    /// Higher = richer summary, more LLM cost. Default: 7.
    /// </summary>
    public int Fanout { get; set; } = 7;
}

/// <summary>Conversation compaction tiers (tool result compression, structured summary).</summary>
public class CompactionOptions
{
    /// <summary>
    /// Tier 1: compress tool results with ContentsJson longer than this (chars).
    /// Only applies to messages outside the keep window. Default: 2000.
    /// </summary>
    public int ToolResultCompressionThreshold { get; set; } = 2000;

    /// <summary>Chars to preserve from the start of compressed tool output. Default: 200.</summary>
    public int ToolResultKeepHeadChars { get; set; } = 200;

    /// <summary>Chars to preserve from the end of compressed tool output. Default: 200.</summary>
    public int ToolResultKeepTailChars { get; set; } = 200;

    /// <summary>Maximum output tokens for the structured summary LLM call. Default: 3000.</summary>
    public int SummaryMaxTokens { get; set; } = 3000;
}

/// <summary>LLM model routing for memory operations.</summary>
public class MemoryModelOptions
{
    /// <summary>
    /// Chat client configuration name for the LLM used in relevance selection.
    /// Must match a model entry in fabrcore.json. Default: "default".
    /// </summary>
    public string RelevanceModelName { get; set; } = "default";

    /// <summary>
    /// Chat client configuration name for the LLM used in compaction operations.
    /// Must match a model entry in fabrcore.json. Default: "default".
    /// </summary>
    public string CompactionModelName { get; set; } = "default";

    /// <summary>
    /// Chat client configuration name for the LLM used in synthetic imagining
    /// (conversation-aware memory query generation).
    /// Must match a model entry in fabrcore.json. Default: "default".
    /// </summary>
    public string ImaginingModelName { get; set; } = "default";

    /// <summary>
    /// Chat client configuration name for the retrieval planner's classification call.
    /// Must match a model entry in fabrcore.json. When blank, falls back to <see cref="RelevanceModelName"/>.
    /// Use a small/cheap model here — the planner only needs to choose between 3 tiers.
    /// Default: "" (uses RelevanceModelName).
    /// </summary>
    public string PlannerModelName { get; set; } = "";

    /// <summary>
    /// Tier-level model name used by any <see cref="LlmModelTier.Small"/> call (relevance selection,
    /// query generation, planner classification, dedup confirmation) when no per-operation override
    /// is explicitly set. Empty = fall back to the per-operation name or "default".
    /// Example: point this at a cheap small-context model to reduce cost by ~30-50% on classification traffic.
    /// Default: "" (falls back to per-operation names).
    /// </summary>
    public string SmallModelName { get; set; } = "";

    /// <summary>
    /// Tier-level model name used by any <see cref="LlmModelTier.Large"/> call (memory merges,
    /// hierarchical summary rollups) when no per-operation override is explicitly set.
    /// Empty = fall back to the per-operation name or "default".
    /// Default: "" (falls back to per-operation names).
    /// </summary>
    public string LargeModelName { get; set; } = "";

    /// <summary>
    /// Resolve the model name to use for a specific LLM call, honoring explicit per-operation
    /// overrides first, then tier-level names, then "default".
    ///
    /// <para>
    /// Precedence: explicit per-operation name (when set and not "default") wins → tier-level
    /// override (when set) → the explicit name as configured (which may be "default").
    /// </para>
    /// </summary>
    /// <param name="tier">The size/cost tier of the call.</param>
    /// <param name="explicitName">
    /// The per-operation name field (e.g., <see cref="RelevanceModelName"/>). The caller passes this
    /// so that explicit user configuration still wins over tier-level defaults.
    /// </param>
    public string ResolveModelForCall(LlmModelTier tier, string explicitName)
    {
        // Explicit, non-"default" per-operation name wins.
        var isExplicit = !string.IsNullOrWhiteSpace(explicitName)
                         && !string.Equals(explicitName, "default", StringComparison.OrdinalIgnoreCase);
        if (isExplicit) return explicitName;

        // Tier-level override, if configured.
        return tier switch
        {
            LlmModelTier.Small when !string.IsNullOrWhiteSpace(SmallModelName) => SmallModelName,
            LlmModelTier.Large when !string.IsNullOrWhiteSpace(LargeModelName) => LargeModelName,
            _ => string.IsNullOrWhiteSpace(explicitName) ? "default" : explicitName
        };
    }
}
