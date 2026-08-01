using System.Text.Json;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Manages the hot layer memory index stored as a single JSON entity row.
/// Enforces entry count and token budget caps, evicting oldest entries when exceeded.
/// All read-modify-write operations run through the store's scope-locked
/// <see cref="IMemoryStore.ModifyIndexContentAsync"/> so concurrent writers on a
/// shared scope cannot lose entries.
/// </summary>
internal class MemoryIndexManager : IMemoryIndexManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IMemoryStore _store;
    private readonly AgentMemoryOptions _options;
    private readonly ILogger<MemoryIndexManager> _logger;

    public MemoryIndexManager(
        IMemoryStore store,
        AgentMemoryOptions options,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _options = options;
        _logger = loggerFactory.CreateLogger<MemoryIndexManager>();
    }

    public async Task<MemoryIndex> GetIndexAsync(string scopeKey, CancellationToken ct = default)
    {
        var json = await _store.GetIndexContentAsync(scopeKey, ct);
        return DeserializeIndex(scopeKey, json);
    }

    public async Task UpdateIndexAsync(string scopeKey, MemoryIndex index, CancellationToken ct = default)
    {
        index.RecalculateTokens();
        var json = JsonSerializer.Serialize(index, JsonOptions);
        await _store.UpsertIndexContentAsync(scopeKey, json, ct);
    }

    public async Task AddIndexEntryAsync(string scopeKey, MemoryIndexEntry entry, CancellationToken ct = default)
    {
        var entryCount = 0;
        var tokenCount = 0;

        await _store.ModifyIndexContentAsync(scopeKey, currentJson =>
        {
            var index = DeserializeIndex(scopeKey, currentJson);

            // Remove existing entry for same memory (in case of update)
            index.Entries.RemoveAll(e => e.MemoryId == entry.MemoryId);

            // Insert at the beginning (newest first)
            index.Entries.Insert(0, entry);

            EnforceCaps(index);

            entryCount = index.Entries.Count;
            tokenCount = index.TotalEstimatedTokens;
            return JsonSerializer.Serialize(index, JsonOptions);
        }, ct);

        _logger.LogDebug("Added index entry for memory {MemoryId} ('{Title}') in scope '{Scope}'. Index size: {Count} entries, ~{Tokens} tokens",
            entry.MemoryId, entry.Title, scopeKey, entryCount, tokenCount);
    }

    public async Task RemoveIndexEntryAsync(string scopeKey, Guid memoryId, CancellationToken ct = default)
    {
        var removed = 0;

        await _store.ModifyIndexContentAsync(scopeKey, currentJson =>
        {
            var index = DeserializeIndex(scopeKey, currentJson);
            removed = index.Entries.RemoveAll(e => e.MemoryId == memoryId);
            if (removed == 0)
                return null; // no change

            index.RecalculateTokens();
            return JsonSerializer.Serialize(index, JsonOptions);
        }, ct);

        if (removed > 0)
            _logger.LogDebug("Removed index entry for memory {MemoryId} from scope '{Scope}'", memoryId, scopeKey);
    }

    public async Task<IReadOnlyList<MemoryIndexEntry>> TruncateIndexAsync(string scopeKey, CancellationToken ct = default)
    {
        var evicted = new List<MemoryIndexEntry>();

        await _store.ModifyIndexContentAsync(scopeKey, currentJson =>
        {
            var index = DeserializeIndex(scopeKey, currentJson);
            evicted = EnforceCaps(index);
            if (evicted.Count == 0)
                return null; // no change

            return JsonSerializer.Serialize(index, JsonOptions);
        }, ct);

        if (evicted.Count > 0)
            _logger.LogInformation("Truncated index for scope '{Scope}': evicted {Count} entries", scopeKey, evicted.Count);

        return evicted;
    }

    private MemoryIndex DeserializeIndex(string scopeKey, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new MemoryIndex();

        try
        {
            return JsonSerializer.Deserialize<MemoryIndex>(json, JsonOptions) ?? new MemoryIndex();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize memory index for scope '{Scope}', returning empty", scopeKey);
            return new MemoryIndex();
        }
    }

    /// <summary>
    /// Enforces max entries and max tokens caps. Entries are sorted newest-first;
    /// eviction removes from the tail (oldest). Returns the evicted entries.
    /// </summary>
    private List<MemoryIndexEntry> EnforceCaps(MemoryIndex index)
    {
        var evicted = new List<MemoryIndexEntry>();

        // Sort by UpdatedAt descending (newest first)
        index.Entries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

        // Enforce entry count cap
        while (index.Entries.Count > _options.HotIndex.MaxEntries)
        {
            var last = index.Entries[^1];
            index.Entries.RemoveAt(index.Entries.Count - 1);
            evicted.Add(last);
        }

        // Enforce token budget cap
        index.RecalculateTokens();
        while (index.TotalEstimatedTokens > _options.HotIndex.MaxTokens && index.Entries.Count > 0)
        {
            var last = index.Entries[^1];
            index.Entries.RemoveAt(index.Entries.Count - 1);
            evicted.Add(last);
            index.RecalculateTokens();
        }

        return evicted;
    }
}
