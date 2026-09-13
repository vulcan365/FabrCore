using FabrCore.Sdk;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Extensions.AI;
using System.Runtime.InteropServices;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class CachedEmbeddingsTests
{
    [TestMethod]
    public async Task Reuse_PreservesOrderDuplicatesAndIndependentVectorStorage()
    {
        var provider = new Provider();
        var cache = new EmbeddingResultCache();
        var cached = new CachedEmbeddings(provider, cache, "db/scope", "model-v1", 2);
        var first = await cached.GetBatchEmbeddings(["bb", "a", "bb"]);
        CollectionAssert.AreEqual(new[] { "bb", "a" }, provider.Calls.Single());
        Assert.AreEqual(2f, first[0].Vector.Span[0]);
        Assert.AreEqual(1f, first[1].Vector.Span[0]);
        Assert.IsTrue(MemoryMarshal.TryGetArray(first[0].Vector, out var array));
        array.Array![array.Offset] = -99;
        Assert.AreEqual(2f, first[2].Vector.Span[0]);
        var warm = await cached.GetBatchEmbeddings(["a", "bb", "ccc"]);
        Assert.AreEqual(2f, warm[1].Vector.Span[0]);
        CollectionAssert.AreEqual(new[] { "ccc" }, provider.Calls.Last());
        Assert.AreEqual(2L, cache.Hits);
        Assert.AreEqual(3f, (await cached.GetEmbeddings("ccc")).Vector.Span[0]);
        Assert.HasCount(2, provider.Calls);
        Assert.IsEmpty(await cached.GetBatchEmbeddings([]));
    }

    [TestMethod]
    public async Task Key_IsolatesScopeModelDimensionsAndExactText()
    {
        var provider = new Provider();
        var cache = new EmbeddingResultCache();
        await new CachedEmbeddings(provider, cache, "scope1", "model1", 2).GetEmbeddings("a");
        await new CachedEmbeddings(provider, cache, "scope1", "model1", 2).GetEmbeddings("a");
        Assert.HasCount(1, provider.Calls);
        await new CachedEmbeddings(provider, cache, "scope2", "model1", 2).GetEmbeddings("a");
        await new CachedEmbeddings(provider, cache, "scope1", "model2", 2).GetEmbeddings("a");
        await new CachedEmbeddings(provider, cache, "scope1", "model1", 2).GetEmbeddings("a ");
        provider.Dimensions = 3;
        await new CachedEmbeddings(provider, cache, "scope1", "model1", 3).GetEmbeddings("a");
        Assert.HasCount(5, provider.Calls);
    }

    [TestMethod]
    public async Task InvalidAndFailedBatches_DoNotPopulateCache()
    {
        foreach (var failure in new[] { "throw", "count", "dimension", "nan", "infinity", "zero" })
        {
            var provider = new Provider { Failure = failure };
            var cached = new CachedEmbeddings(provider, new EmbeddingResultCache(), "scope", "model", 2);
            await Assert.ThrowsAsync<InvalidOperationException>(() => cached.GetBatchEmbeddings(["a", "bb"]));
            provider.Failure = null;
            await cached.GetBatchEmbeddings(["a", "bb"]);
            CollectionAssert.AreEqual(new[] { "a", "bb" }, provider.Calls.Last());
        }
    }

    [TestMethod]
    public async Task Cache_ExpiresAndEvicts()
    {
        var clock = new Clock();
        var provider = new Provider();
        var cache = new EmbeddingResultCache(1, TimeSpan.FromMinutes(1), clock);
        var cached = new CachedEmbeddings(provider, cache, "scope", "model", 2);
        await cached.GetEmbeddings("a");
        await cached.GetEmbeddings("b");
        await cached.GetEmbeddings("a");
        Assert.HasCount(3, provider.Calls);
        clock.Now += TimeSpan.FromMinutes(1);
        await cached.GetEmbeddings("a");
        Assert.HasCount(4, provider.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Provider : IEmbeddings
    {
        public List<string[]> Calls { get; } = [];
        public string? Failure;
        public int Dimensions = 2;
        public Task<Embedding<float>> GetEmbeddings(string text) => throw new NotSupportedException();
        public Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
        {
            Calls.Add(texts.ToArray());
            if (Failure == "throw") throw new InvalidOperationException("Test provider failure");
            var result = texts.Select(t => new Embedding<float>(Enumerable.Repeat((float)t.Length,
                Failure == "dimension" ? 1 : Dimensions).ToArray())).ToArray();
            if (Failure == "count") result = result[..^1];
            if (Failure is "nan" or "infinity" or "zero") result[^1] = new Embedding<float>(
                Enumerable.Repeat(Failure == "nan" ? float.NaN : Failure == "infinity" ? float.PositiveInfinity : 0f, Dimensions).ToArray());
            return Task.FromResult<IReadOnlyList<Embedding<float>>>(result);
        }
    }
}
