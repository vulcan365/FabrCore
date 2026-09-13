using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Three-stage memory retrieval pipeline:
/// 1. Cheap metadata scan (headers only)
/// 2. LLM-based relevance selection from the manifest
/// 3. Full content retrieval for selected memories
/// </summary>
public interface IMemoryRetriever
{
    /// <summary>
    /// Stage 1: Scan memory headers (metadata only, no content or embeddings).
    /// Returns up to <paramref name="limit"/> headers sorted by UpdatedAt descending.
    /// </summary>
    Task<IReadOnlyList<MemoryHeader>> ScanMemoryHeadersAsync(
        string scopeKey, int limit, MemoryType? typeFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Stage 2: Use an LLM to select the most relevant memories from the manifest
    /// for the given query. Falls back to manifest recency order if the LLM call fails.
    /// </summary>
    /// <param name="query">The current user query or context.</param>
    /// <param name="manifest">The list of available memory headers to choose from.</param>
    /// <param name="maxToSelect">Maximum number of memories to select.</param>
    /// <param name="excludeIds">IDs of memories already surfaced in the current conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of selected memory IDs.</returns>
    Task<IReadOnlyList<Guid>> SelectRelevantMemoriesAsync(
        string query, IReadOnlyList<MemoryHeader> manifest, int maxToSelect,
        IReadOnlySet<Guid>? excludeIds = null, CancellationToken ct = default);

    /// <summary>
    /// Stage 3: Load the full content of a memory by ID.
    /// </summary>
    Task<MemoryEntry?> RetrieveMemoryAsync(
        string scopeKey, Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Compute a freshness warning for a memory header.
    /// Returns null if the memory is within the freshness threshold.
    /// </summary>
    string? GetFreshnessWarning(MemoryHeader header);

    /// <summary>
    /// Graph-aware retrieval: traverse relationships from seed entities to find connected knowledge.
    /// Returns entities reachable within <paramref name="maxHops"/> hops, with their primary chunk content loaded.
    /// </summary>
    /// <param name="scopeKey">Agent handle for scoping.</param>
    /// <param name="seedEntityIds">Entity IDs to start the traversal from.</param>
    /// <param name="maxHops">Maximum relationship hops (1 = direct neighbors only).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MemoryEntry>> GetRelatedEntitiesAsync(
        string scopeKey, IReadOnlyList<Guid> seedEntityIds, int maxHops = 1,
        CancellationToken ct = default);
}
