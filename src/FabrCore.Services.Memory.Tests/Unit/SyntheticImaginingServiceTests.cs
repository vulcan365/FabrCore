using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Services.Memory.Tests.Infrastructure;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class SyntheticImaginingServiceTests
{
    [TestMethod]
    public async Task ImagineAsync_ExecutesCappedQueriesAndDeduplicatesResults()
    {
        var a = Entry("A");
        var b = Entry("B");
        var c = Entry("C");
        var d = Entry("D");
        var e = Entry("E");
        var memory = Substitute.For<IAgentMemoryService>();
        memory.RecallAsync("query one", Arg.Any<IReadOnlySet<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryRecallResult
            {
                HotIndex = new MemoryIndex { Entries = [new MemoryIndexEntry { MemoryId = a.Id, Title = "A" }] },
                WarmMemories = [a, b],
                FreshnessWarnings = ["warning"]
            });
        memory.RecallAsync("query two", Arg.Any<IReadOnlySet<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryRecallResult
            {
                WarmMemories = [b, c],
                FreshnessWarnings = ["warning"]
            });
        memory.SearchArchiveAsync("query one", 10, null, Arg.Any<CancellationToken>())
            .Returns(new[] { Search(d, 0.4), Search(a, 0.5) });
        memory.SearchArchiveAsync("query two", 10, null, Arg.Any<CancellationToken>())
            .Returns(new[] { Search(d, 0.1), Search(e, 0.2) });
        var provider = Substitute.For<IAgentMemoryProvider>();
        provider.GetMemoryService("scope").Returns(memory);
        var options = new AgentMemoryOptions();
        options.Retrieval.MaxImaginingQueries = 2;
        var client = FakeChatClient.WithText(
            """{"queries":["query one","query two","query three"]}""");
        var service = CreateService(provider, options, client);
        var surfaced = new HashSet<Guid> { Guid.NewGuid() };

        var result = await service.ImagineAsync(
            [new ChatMessage(ChatRole.User, "We are discussing the release workflow in detail.")],
            "What should I do next?", "scope", surfaced);

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { "query one", "query two" }, result.GeneratedQueries);
        CollectionAssert.AreEquivalent(
            new[] { a.Id, b.Id, c.Id }, result.AggregatedRecall.WarmMemories.Select(x => x.Id).ToArray());
        CollectionAssert.AreEqual(new[] { d.Id, e.Id, a.Id }, result.ArchiveResults.Select(x => x.Entry.Id).ToArray());
        Assert.AreEqual(0.1, result.ArchiveResults[0].Distance, 0.0001);
        Assert.AreEqual(5, result.UniqueMemoryCount);
        Assert.HasCount(1, result.AggregatedRecall.FreshnessWarnings);
        await memory.Received(2).RecallAsync(
            Arg.Any<string>(), surfaced, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ImagineAsync_StripsExistingMemoryContextFromLlmPrompt()
    {
        var memory = Substitute.For<IAgentMemoryService>();
        memory.RecallAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryRecallResult());
        memory.SearchArchiveAsync(Arg.Any<string>(), 10, null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemorySearchResult>());
        var provider = Substitute.For<IAgentMemoryProvider>();
        provider.GetMemoryService("scope").Returns(memory);
        var client = FakeChatClient.WithText("""{"queries":["deployment workflow"]}""");
        var service = CreateService(provider, new AgentMemoryOptions(), client);

        await service.ImagineAsync(
            [new ChatMessage(ChatRole.Assistant,
                $"Relevant: {AgentMemoryService.MemoryContextStart}secret recalled content{AgentMemoryService.MemoryContextEnd}")],
            "How do we deploy this release?", "scope");

        var prompt = string.Join("\n", client.ReceivedMessages.Single().Select(m => m.Text));
        Assert.DoesNotContain("secret recalled content", prompt);
    }

    [TestMethod]
    public async Task ImagineAsync_BlankMessageReturnsEmptySuccessWithoutLlmCall()
    {
        var provider = Substitute.For<IAgentMemoryProvider>();
        var client = FakeChatClient.WithText("should not be called");

        var result = await CreateService(provider, new AgentMemoryOptions(), client)
            .ImagineAsync([], " ", "scope");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, client.CallCount);
        provider.DidNotReceive().GetMemoryService(Arg.Any<string>());
    }

    [TestMethod]
    public async Task ImagineAsync_DownstreamFailureReturnsGracefulError()
    {
        var provider = Substitute.For<IAgentMemoryProvider>();
        provider.GetMemoryService("scope").Returns(_ => throw new InvalidOperationException("scope unavailable"));
        var client = FakeChatClient.WithText("""{"queries":["query"]}""");

        var result = await CreateService(provider, new AgentMemoryOptions(), client)
            .ImagineAsync([], "Find prior context", "scope");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "scope unavailable");
    }

    private static SyntheticImaginingService CreateService(
        IAgentMemoryProvider provider, AgentMemoryOptions options, FakeChatClient client)
    {
        var services = new ServiceCollection()
            .AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(client))
            .BuildServiceProvider();
        return new SyntheticImaginingService(provider, options, services, NullLoggerFactory.Instance);
    }

    private static MemoryEntry Entry(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, Type = MemoryType.Fact
    };

    private static MemorySearchResult Search(MemoryEntry entry, double distance) => new()
    {
        Entry = entry, Distance = distance
    };
}
