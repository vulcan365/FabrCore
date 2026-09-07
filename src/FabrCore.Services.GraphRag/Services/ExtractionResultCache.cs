using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>Optional process-local storage for validated graph extraction responses. Register as a singleton.</summary>
public sealed class ExtractionResultCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Text, DateTimeOffset Expires)> _entries = [];
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private long _hits;
    public long Hits => Interlocked.Read(ref _hits);

    public ExtractionResultCache(int capacity = 256, TimeSpan? timeToLive = null, TimeProvider? clock = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = timeToLive ?? TimeSpan.FromMinutes(30);
        if (_ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeToLive));
        _clock = clock ?? TimeProvider.System;
    }

    internal static string Key(params string[] components) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(components))));

    internal string? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (entry.Expires <= _clock.GetUtcNow()) { _entries.Remove(key); return null; }
            Interlocked.Increment(ref _hits);
            return entry.Text;
        }
    }

    internal void Put(string key, string validatedResponse)
    {
        // Bound both entry count and response size (at most ~128 MiB of UTF-16 text by default).
        if (validatedResponse.Length > 262_144) return;
        lock (_gate)
        {
            if (!_entries.ContainsKey(key) && _entries.Count >= _capacity)
                _entries.Remove(_entries.MinBy(e => e.Value.Expires).Key);
            _entries[key] = (validatedResponse, _clock.GetUtcNow() + _ttl);
        }
    }
}
