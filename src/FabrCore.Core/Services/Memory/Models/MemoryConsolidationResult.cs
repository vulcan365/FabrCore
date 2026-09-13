namespace FabrCore.Services.Memory.Models;

/// <summary>
/// Statistics from a memory consolidation (compaction) run.
/// </summary>
public class MemoryConsolidationResult
{
    /// <summary>Number of duplicate memory pairs that were merged.</summary>
    public int DuplicatesMerged { get; set; }

    /// <summary>Number of stale memories that were pruned.</summary>
    public int StaleMemoriesPruned { get; set; }

    /// <summary>Number of contradictions that were resolved.</summary>
    public int ContradictionsResolved { get; set; }

    /// <summary>Number of index entries evicted during truncation.</summary>
    public int IndexEntriesEvicted { get; set; }

    /// <summary>
    /// Number of hierarchical summary-tree nodes materialized. Zero when
    /// <c>AgentMemoryOptions.SummaryTreeEnabled</c> is false.
    /// </summary>
    public int SummaryNodesBuilt { get; set; }

    /// <summary>When this consolidation completed.</summary>
    public DateTime ConsolidatedAt { get; set; }
}
