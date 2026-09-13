using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Memory consolidation engine. Performs deduplication, staleness pruning,
/// contradiction resolution, and index truncation to keep the memory store
/// within bounded budgets.
/// </summary>
public interface IMemoryCompactor
{
    /// <summary>
    /// Run a full consolidation pass: dedup, prune stale, resolve contradictions, truncate index.
    /// </summary>
    Task<MemoryConsolidationResult> ConsolidateAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>
    /// Find and merge duplicate memories (vector distance below threshold with same type).
    /// Returns the number of duplicates merged.
    /// </summary>
    Task<int> DeduplicateAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>
    /// Prune memories that are stale and no longer relevant.
    /// Returns the number of memories pruned.
    /// </summary>
    Task<int> PruneStaleAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>
    /// Identify and resolve contradictions between recent memories.
    /// Returns the number of contradictions resolved.
    /// </summary>
    Task<int> ResolveContradictionsAsync(string scopeKey, CancellationToken ct = default);
}
