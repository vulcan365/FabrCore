using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Services.Memory.Tests.Infrastructure;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryRetrieverTests
{
    [TestMethod]
    public async Task SelectRelevantMemories_FiltersSurfacedIdsBeforeShortCircuit()
    {
        var store = Substitute.For<IMemoryStore>();
        var retriever = CreateRetriever(store);
        var headers = Headers(3);

        var selected = await retriever.SelectRelevantMemoriesAsync(
            "query", headers, 5, new HashSet<Guid> { headers[1].MemoryId });

        CollectionAssert.AreEqual(
            new[] { headers[0].MemoryId, headers[2].MemoryId }, selected.ToArray());
    }

    [TestMethod]
    public async Task SelectRelevantMemories_LlmCanOnlyReturnManifestIds()
    {
        var store = Substitute.For<IMemoryStore>();
        var headers = Headers(4);
        var unknown = Guid.NewGuid();
        var client = FakeChatClient.WithText(
            $$"""
            ```json
            {"selected_memories":["{{headers[2].MemoryId}}","{{unknown}}"]}
            ```
            """);
        var retriever = CreateRetriever(store, client);

        var selected = await retriever.SelectRelevantMemoriesAsync("relevant query", headers, 2);

        CollectionAssert.AreEqual(new[] { headers[2].MemoryId }, selected.ToArray());
    }

    [TestMethod]
    public async Task SelectRelevantMemories_NoLlmFallsBackToManifestRecencyWithoutVectorCall()
    {
        var store = Substitute.For<IMemoryStore>();
        var headers = Headers(4);
        var retriever = CreateRetriever(store);

        var selected = await retriever.SelectRelevantMemoriesAsync("query", headers, 2);

        CollectionAssert.AreEqual(
            new[] { headers[0].MemoryId, headers[1].MemoryId }, selected.ToArray());
        await store.DidNotReceive().GenerateEmbeddingAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().VectorSearchAsync(
            Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
            Arg.Any<MemoryType?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RetrieveMemory_LoadsPrimaryChunkContent()
    {
        var store = Substitute.For<IMemoryStore>();
        var id = Guid.NewGuid();
        store.GetEntityByIdAsync("scope", id, Arg.Any<CancellationToken>())
            .Returns(new MemoryEntry { Id = id, Title = "title" });
        store.GetPrimaryChunkAsync("scope", id, Arg.Any<CancellationToken>())
            .Returns(new MemoryChunkEntry { EntityId = id, Content = "full content", Embedding = [1, 0] });

        var result = await CreateRetriever(store).RetrieveMemoryAsync("scope", id);

        Assert.IsNotNull(result);
        Assert.AreEqual("full content", result.Content);
        CollectionAssert.AreEqual(new float[] { 1, 0 }, result.Embedding!);
    }

    [TestMethod]
    public void GetFreshnessWarning_DistinguishesDurableFreshAndSnapshotMemories()
    {
        var options = new AgentMemoryOptions();
        options.Retrieval.FreshnessDaysThreshold = 2;
        var retriever = CreateRetriever(Substitute.For<IMemoryStore>(), options: options);

        Assert.IsNull(retriever.GetFreshnessWarning(new MemoryHeader
        {
            UpdatedAt = DateTime.UtcNow.AddHours(-2)
        }));
        StringAssert.Contains(retriever.GetFreshnessWarning(new MemoryHeader
        {
            UpdatedAt = DateTime.UtcNow.AddDays(-3)
        }), "[Stale:");
        StringAssert.Contains(retriever.GetFreshnessWarning(new MemoryHeader
        {
            UpdatedAt = DateTime.UtcNow,
            IsPointInTime = true
        }), "[Snapshot:");
    }

    private static MemoryRetriever CreateRetriever(
        IMemoryStore store,
        FakeChatClient? client = null,
        AgentMemoryOptions? options = null)
    {
        var services = new ServiceCollection();
        if (client is not null)
            services.AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(client));
        return new MemoryRetriever(
            store,
            options ?? new AgentMemoryOptions(),
            services.BuildServiceProvider(),
            NullLoggerFactory.Instance);
    }

    private static List<MemoryHeader> Headers(int count) =>
        Enumerable.Range(0, count).Select(i => new MemoryHeader
        {
            MemoryId = Guid.NewGuid(),
            Title = $"memory {i}",
            Type = MemoryType.Fact,
            Description = $"description {i}",
            UpdatedAt = DateTime.UtcNow.AddMinutes(-i)
        }).ToList();
}
