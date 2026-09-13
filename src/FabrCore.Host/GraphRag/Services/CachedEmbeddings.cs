using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>Bounded process-local vector storage. Share one instance across scoped decorators.</summary>
public sealed class EmbeddingResultCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (float[] Vector, DateTimeOffset Expires, LinkedListNode<string> Node)> _entries = [];
    private readonly LinkedList<string> _order = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private long _hits;
    public long Hits => Interlocked.Read(ref _hits);

    public EmbeddingResultCache(int capacity = 8192, TimeSpan? timeToLive = null, TimeProvider? clock = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _ttl = timeToLive ?? TimeSpan.FromMinutes(30);
        if (_ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeToLive));
        _clock = clock ?? TimeProvider.System;
    }

    internal float[]? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (entry.Expires <= _clock.GetUtcNow()) { _entries.Remove(key); _order.Remove(entry.Node); return null; }
            Interlocked.Increment(ref _hits);
            return (float[])entry.Vector.Clone();
        }
    }

    internal void Put(string key, float[] vector)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var previous)) _order.Remove(previous.Node);
            else if (_entries.Count >= _capacity)
            {
                _entries.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            _entries[key] = ((float[])vector.Clone(), _clock.GetUtcNow() + _ttl, _order.AddLast(key));
        }
    }
}

/// <summary>
/// Opt-in exact embedding reuse. Supply a namespace including database/tenant/scope and an
/// identity for the actual provider, deployment, revision and preprocessing configuration.
/// Construct a new decorator when either changes. Does not own or dispose the inner provider.
/// </summary>
public sealed class CachedEmbeddings : IEmbeddings
{
    private readonly IEmbeddings _inner;
    private readonly EmbeddingResultCache _cache;
    private readonly string _partition;
    private readonly int _dimensions;

    public CachedEmbeddings(IEmbeddings inner, EmbeddingResultCache cache,
        string scopeNamespace, string modelIdentity, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        if (dimensions is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(dimensions));
        _inner = inner;
        _cache = cache;
        _dimensions = dimensions;
        _partition = ExtractionResultCache.Key("embedding-cache-v1", scopeNamespace, modelIdentity, dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<Embedding<float>> GetEmbeddings(string text)
        => (await GetBatchEmbeddings([text]))[0];

    public async Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
    {
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var text in texts.Distinct(StringComparer.Ordinal))
        {
            var value = _cache.Get(ExtractionResultCache.Key(_partition, text));
            if (value is null) missing.Add(text);
            else vectors.Add(text, value);
        }
        if (missing.Count > 0)
        {
            var generated = await _inner.GetBatchEmbeddings(missing);
            if (generated.Count != missing.Count) throw new InvalidOperationException("Embedding response count does not match the requested inputs.");
            // Validate the entire response before storing anything. A failed call never seeds the cache.
            var fresh = generated.Select(e => e.Vector.ToArray()).ToArray();
            if (fresh.Any(v => v.Length != _dimensions || v.Any(x => !float.IsFinite(x)) || v.All(x => x == 0)))
                throw new InvalidOperationException("Embedding response contains an invalid dimension, nonfinite or zero vector.");
            for (var i = 0; i < missing.Count; i++)
            {
                _cache.Put(ExtractionResultCache.Key(_partition, missing[i]), fresh[i]);
                vectors.Add(missing[i], fresh[i]);
            }
        }
        // Independent arrays protect cached values and duplicate outputs from downstream mutation.
        return texts.Select(t => new Embedding<float>((float[])vectors[t].Clone())).ToArray();
    }
}
