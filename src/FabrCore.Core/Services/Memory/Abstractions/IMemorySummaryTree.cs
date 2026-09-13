using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Builds and queries the agent's hierarchical semantic summary tree.
/// Rolls up memories into topic-level natural-language summaries so broad queries can resolve
/// against the tree instead of fanning out across every individual memory header.
/// </summary>
public interface IMemorySummaryTree
{
    /// <summary>
    /// Rebuild the summary tree for the given agent from its current warm memories. Typically called
    /// as the final step of <c>ConsolidateAsync</c> — the summary tree is a derived artifact and
    /// stays authoritative only as long as the underlying memories are stable.
    /// </summary>
    /// <returns>Number of summary nodes materialized.</returns>
    Task<int> BuildAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>
    /// Vector-search the summary tree for nodes relevant to the query, newest/most-relevant first.
    /// Returns an empty list when no summary tree exists for the agent.
    /// </summary>
    Task<IReadOnlyList<MemorySummaryNode>> QueryAsync(
        string scopeKey, string query, int limit = 5, CancellationToken ct = default);

    /// <summary>
    /// Get all summary nodes for an agent (metadata browser / debugging). Not intended for the
    /// retrieval hot path — use <see cref="QueryAsync"/> there.
    /// </summary>
    Task<IReadOnlyList<MemorySummaryNode>> GetAllAsync(
        string scopeKey, CancellationToken ct = default);

    /// <summary>Delete all summary nodes for an agent. Used when the user wipes memory.</summary>
    Task ClearAsync(string scopeKey, CancellationToken ct = default);
}
