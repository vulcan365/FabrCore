using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Manages the hot layer memory index — a bounded table-of-contents that is always
/// injected into agent context. Enforces entry count and token budget caps.
/// </summary>
public interface IMemoryIndexManager
{
    /// <summary>Get the current memory index for an agent. Returns an empty index if none exists.</summary>
    Task<MemoryIndex> GetIndexAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>Replace the entire memory index for an agent.</summary>
    Task UpdateIndexAsync(string scopeKey, MemoryIndex index, CancellationToken ct = default);

    /// <summary>
    /// Add an entry to the memory index. If the index exceeds caps after insertion,
    /// the oldest entries are evicted (they remain as warm memories, just lose their hot pointer).
    /// </summary>
    Task AddIndexEntryAsync(string scopeKey, MemoryIndexEntry entry, CancellationToken ct = default);

    /// <summary>Remove an entry from the memory index by memory ID.</summary>
    Task RemoveIndexEntryAsync(string scopeKey, Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Truncate the index to fit within configured caps (max entries and max tokens).
    /// Returns the list of evicted entries.
    /// </summary>
    Task<IReadOnlyList<MemoryIndexEntry>> TruncateIndexAsync(string scopeKey, CancellationToken ct = default);
}
