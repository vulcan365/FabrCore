using System.Collections.Concurrent;
using System.Diagnostics;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.EvalConsole;

internal sealed record EmbeddingCallSample(int Inputs, long Characters, long ElapsedMs, string? Error);

// Place inside the cache decorator to measure only calls that reach the actual provider.
internal sealed class MeasuredEmbeddings(IEmbeddings inner) : IEmbeddings
{
    private readonly ConcurrentQueue<EmbeddingCallSample> _calls = new();
    public List<EmbeddingCallSample> Drain()
    {
        var result = new List<EmbeddingCallSample>();
        while (_calls.TryDequeue(out var call)) result.Add(call);
        return result;
    }

    public async Task<Embedding<float>> GetEmbeddings(string text)
        => (await Measure([text], async () => new[] { await inner.GetEmbeddings(text) }))[0];

    public Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
        => Measure(texts, () => inner.GetBatchEmbeddings(texts));

    private async Task<IReadOnlyList<Embedding<float>>> Measure(IReadOnlyList<string> texts,
        Func<Task<IReadOnlyList<Embedding<float>>>> generate)
    {
        var target = _calls;
        var sw = Stopwatch.StartNew();
        string? error = null;
        try { return await generate(); }
        catch (Exception ex) { error = ex.GetType().Name; throw; }
        finally { target.Enqueue(new(texts.Count, texts.Sum(t => (long)t.Length), sw.ElapsedMilliseconds, error)); }
    }
}

