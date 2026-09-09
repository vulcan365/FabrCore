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
    [DataRow(false)]
    [DataRow(true)]
    public async Task SemanticCandidatesDoNotBecomeMemoriesWhenSelectionIsUnavailable(bool malformed)
    {
        var options = new AgentMemoryOptions(); options.Retrieval.UseSemanticCandidates = true;
        var retriever = CreateRetriever(Substitute.For<IMemoryStore>(),
            malformed ? FakeChatClient.WithText("malformed") : null, options);
        Assert.IsEmpty(await retriever.SelectRelevantMemoriesAsync("unknown", Headers(4), 3));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VerifierFailurePreservesInitialIdsButCancellationPropagates(bool cancel)
    {
        var headers = Headers(3);
        var options = new AgentMemoryOptions(); options.Retrieval.UseCompactSelectionIds = true;
        options.Retrieval.VerifyMultiMemorySelection = true;
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var client = new FakeChatClient(_ => {
            if (++count == 1) return new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.Assistant, "{\"selected_memories\":[\"0\",\"2\"]}"));
            if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            throw new InvalidOperationException("verification unavailable");
        });
        var retriever = CreateRetriever(Substitute.For<IMemoryStore>(), client, options);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => retriever.SelectRelevantMemoriesAsync("query", headers, 3, ct: cancellation.Token));
        else CollectionAssert.AreEqual(new[] { headers[0].MemoryId, headers[2].MemoryId },
            (await retriever.SelectRelevantMemoriesAsync("query", headers, 3)).ToArray());
    }

    [TestMethod]
    [DataRow("{\"selected_memories\":[\"1\"]}", 1)]
    [DataRow("{\"selected_memories\":[\"0\",\"1\"]}", 2)]
    [DataRow("{\"selected_memories\":[]}", 0)]
    [DataRow("{\"selected_memories\":[\"999\"]}", 2)]
    [DataRow("malformed", 2)]
    public async Task VerifierIsBoundedToSelectedEvidenceAndPreservesSelectionOnInvalidOutput(string response, int expectedCount)
    {
        var headers = Headers(3);
        var options = new AgentMemoryOptions();
        options.Retrieval.UseCompactSelectionIds = true;
        options.Retrieval.VerifyMultiMemorySelection = true;
        var client = FakeChatClient.WithSequentialResponses("{\"selected_memories\":[\"0\",\"2\"]}", response);
        var selected = await CreateRetriever(Substitute.For<IMemoryStore>(), client, options)
            .SelectRelevantMemoriesAsync("query", headers, 3);
        Assert.HasCount(expectedCount, selected);
        Assert.IsFalse(selected.Contains(headers[1].MemoryId));
        if (expectedCount == 1) Assert.AreEqual(headers[2].MemoryId, selected[0]);
        Assert.AreEqual(2, client.CallCount);
        Assert.IsFalse(client.ReceivedMessages.Last().Any(message => message.Text.Contains(headers[1].Description!)));
    }

    [TestMethod]
    [DataRow("{\"selected_memories\":[]}")]
    [DataRow("{\"selected_memories\":[\"0\"]}")]
    public async Task VerifierDoesNotAddCallsForZeroOrOneMemory(string response)
    {
        var options = new AgentMemoryOptions(); options.Retrieval.UseCompactSelectionIds = true;
        options.Retrieval.VerifyMultiMemorySelection = true;
        var client = FakeChatClient.WithText(response);
        await CreateRetriever(Substitute.For<IMemoryStore>(), client, options).SelectRelevantMemoriesAsync("query", Headers(3), 3);
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    public async Task CompactLabelsMapOnlyWithinFilteredInvocationAndRemainBounded()
    {
        var options = new AgentMemoryOptions(); options.Retrieval.UseCompactSelectionIds = true;
        var headers = Headers(4);
        var client = FakeChatClient.WithText("{\"selected_memories\":[\"1\",\"1\",\"900\",\"0\"]}");
        var retriever = CreateRetriever(Substitute.For<IMemoryStore>(), client, options);
        var selected = await retriever.SelectRelevantMemoriesAsync("query", headers, 1, new HashSet<Guid> { headers[0].MemoryId });
        CollectionAssert.AreEqual(new[] { headers[2].MemoryId }, selected.ToArray());
        Assert.IsFalse(client.ReceivedMessages.SelectMany(m => m).Any(m => m.Text.Contains(headers[2].MemoryId.ToString("N"))));
        var otherHeaders = Headers(4);
        var second = await retriever.SelectRelevantMemoriesAsync("other query", otherHeaders, 1);
        CollectionAssert.AreEqual(new[] { otherHeaders[1].MemoryId }, second.ToArray());
    }

    [TestMethod]
    public async Task CompactLabelsPreserveAbstentionAndRejectGuidResponses()
    {
        var options = new AgentMemoryOptions(); options.Retrieval.UseCompactSelectionIds = true;
        var headers = Headers(2);
        foreach (var response in new[] { "{\"selected_memories\":[]}",
            System.Text.Json.JsonSerializer.Serialize(new { selected_memories = new[] { headers[0].MemoryId.ToString("N") } }) })
        {
            var retriever = CreateRetriever(Substitute.For<IMemoryStore>(), FakeChatClient.WithText(response), options);
            Assert.IsEmpty(await retriever.SelectRelevantMemoriesAsync("unknown", headers, 2));
        }
    }

    [TestMethod]
    public async Task EmptySelectionRemainsEmptyEvenForSmallPools()
    {
        var client = FakeChatClient.WithText("{\"selected_memories\":[]}");
        var retriever = CreateRetriever(Substitute.For<IMemoryStore>(), client);
        var selected = await retriever.SelectRelevantMemoriesAsync("unknown", Headers(1), 5);
        Assert.IsEmpty(selected);
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    public async Task SelectionIsDistinctAndBounded()
    {
        var headers = Headers(3);
        var text = System.Text.Json.JsonSerializer.Serialize(new { selected_memories = new[] {
            headers[0].MemoryId, headers[0].MemoryId, headers[1].MemoryId, headers[2].MemoryId } });
        var retriever = CreateRetriever(Substitute.For<IMemoryStore>(), FakeChatClient.WithText(text));
        Assert.HasCount(2, await retriever.SelectRelevantMemoriesAsync("query", headers, 2));
    }
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
