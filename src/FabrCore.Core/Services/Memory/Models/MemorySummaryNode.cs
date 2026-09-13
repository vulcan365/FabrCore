namespace FabrCore.Services.Memory.Models;

/// <summary>
/// A node in the agent's hierarchical semantic summary tree. Each node rolls up a set of child
/// memories (or child summary nodes at deeper levels) into a natural-language summary the agent
/// can retrieve instead of fanning out over every underlying memory header.
///
/// <para>
/// Inspired by LinkedIn's Cognitive Memory Agent: hierarchical NL summaries reduce LLM calls and
/// context size vs. flat RAG — a broad query can resolve at the tree layer without loading individual
/// memories, and a narrow query can drill down into the relevant subtree only.
/// </para>
/// </summary>
public class MemorySummaryNode
{
    /// <summary>Unique identifier.</summary>
    public Guid NodeId { get; set; }

    /// <summary>Agent handle that owns this summary.</summary>
    public string ScopeKey { get; set; } = "";

    /// <summary>
    /// Parent node ID — null for roots. Deeper nodes summarize a subset of their parent's material.
    /// </summary>
    public Guid? ParentNodeId { get; set; }

    /// <summary>0 = root (broadest rollup), 1 = mid-level, 2+ = leaf-adjacent.</summary>
    public int Depth { get; set; }

    /// <summary>
    /// Short topic label (e.g., <c>"Customer onboarding knowledge"</c>). Used as the heading when
    /// the node is presented to the agent LLM.
    /// </summary>
    public string Topic { get; set; } = "";

    /// <summary>Natural-language summary of the underlying memories/subtree.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Vector embedding of <c>Topic + Summary</c>. Used for query-time retrieval.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>How many leaf memories ultimately roll up into this node.</summary>
    public int MemberCount { get; set; }

    /// <summary>When the node was first materialized.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the node was last rebuilt.</summary>
    public DateTime UpdatedAt { get; set; }
}
