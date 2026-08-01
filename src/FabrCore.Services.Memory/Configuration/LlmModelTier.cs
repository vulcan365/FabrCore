namespace FabrCore.Services.Memory.Configuration;

/// <summary>
/// Size/cost tier for an LLM call inside the memory system. Each call site tags itself with a tier
/// so users can globally point tier=Small at a cheap model (classification, query generation, planner)
/// while keeping tier=Large on a flagship model for creative/high-stakes work (content merges, summaries).
///
/// <para>
/// The tier is a <i>request</i>, not a command: per-operation overrides like
/// <c>AgentMemoryOptions.RelevanceModelName</c> still win when the user sets them explicitly.
/// </para>
/// </summary>
public enum LlmModelTier
{
    /// <summary>
    /// Cheap classification calls: relevance selection, duplicate detection, query generation,
    /// retrieval-planner tier decisions. These fit on a small model with minimal quality loss.
    /// </summary>
    Small,

    /// <summary>
    /// Reasoning-heavy calls: contradiction resolution, structured memory extraction, staleness
    /// confirmation. These benefit from a mid-tier model but do not need flagship quality.
    /// </summary>
    Default,

    /// <summary>
    /// High-stakes generation: merging two near-duplicate memories into a single coherent entry,
    /// producing hierarchical semantic summaries. Quality matters here — wrong merges lose knowledge.
    /// </summary>
    Large
}
