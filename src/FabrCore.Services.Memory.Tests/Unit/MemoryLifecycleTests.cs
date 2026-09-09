using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Services.Memory.Tests.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class MemoryLifecycleTests
{
    [TestMethod]
    public void DuplicateMemoryToolsFailBeforeChangingHarnessConfiguration()
    {
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ScopeKey.Returns("core");
        var options = new FabrCoreHarnessOptions().WithMemory(memory, includeTools: true);
        var providers = options.AIContextProviders;
        Assert.Throws<ArgumentException>(() => options.WithMemory(memory, includeTools: true));
        Assert.AreSame(providers, options.AIContextProviders);
        Assert.HasCount(8, options.ChatOptions!.Tools!);
    }

    [TestMethod]
    public async Task HarnessMemoryCompactionPropagatesFailuresAndPreservesHistory()
    {
        var (host, history) = History();
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ExtractMemoriesAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<MemoryEntry>>>(_ => throw new InvalidOperationException("storage unavailable"));
        var service = new MemoryAwareCompactionService(NullLoggerFactory.Instance, new TestChatClientService(FakeChatClient.WithText("summary")));
        var handler = new MemoryCompactionHandler(memory, service, new(), NullLoggerFactory.Instance);
        var options = new FabrCoreHarnessOptions().WithMemory(memory).WithMemoryCompaction(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => options.HistoryCompaction!(history, Config()));
        await host.DidNotReceive().ReplaceThreadMessagesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<StoredChatMessage>>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExtractMemoriesAsync(
            new List<ChatMessage> { new(ChatRole.User, "remember this") }, keepLastN: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => handler.ExtractMemoriesAsync(new List<ChatMessage>(), -1));
    }

    [TestMethod]
    public async Task MemoryToolsAreScopedAndPassCancellationWithoutExposingItToTheModel()
    {
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ScopeKey.Returns("own");
        var tools = MemoryHarnessExtensions.CreateMemoryTools(memory);
        var risks = MemoryHarnessExtensions.GetMemoryToolRisks(tools);
        var save = tools.Cast<AIFunction>().Single(t => t.Name == "SaveMemory");
        Assert.AreEqual(InternalAgentToolRisk.MemoryWrite, risks[save.Name]);
        Assert.AreEqual("own", ((IScopedAgentMemoryTool)save).WriteScope);
        Assert.DoesNotContain("ct", save.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await save.InvokeAsync(new AIFunctionArguments {
            ["title"] = "note", ["type"] = "Fact", ["content"] = "content"
        }, cancel.Token));
        await memory.DidNotReceiveWithAnyArgs().SaveMemoryAsync(default!, default, default!);
        Assert.Throws<ArgumentException>(() => MemoryHarnessExtensions.GetMemoryToolRisks([AIFunctionFactory.Create(() => "not memory")]));
    }

    [TestMethod]
    public async Task LayeredMemoryReadsBothButMutatesOnlyOwn()
    {
        var provider = Substitute.For<IAgentMemoryProvider>();
        var core = Substitute.For<IAgentMemoryService>();
        var own = Substitute.For<IAgentMemoryService>();
        provider.GetMemoryService("core").Returns(core);
        provider.GetMemoryService("internal:4:core:research").Returns(own);
        var c = new MemoryEntry { Id = Guid.NewGuid(), ScopeKey = "core", Content = "core" };
        var o = new MemoryEntry { Id = Guid.NewGuid(), ScopeKey = "internal:4:core:research", Content = "own" };
        core.RecallAsync("q", null, default).Returns(new MemoryRecallResult { WarmMemories = [c] });
        own.RecallAsync("q", null, default).Returns(new MemoryRecallResult { WarmMemories = [o] });
        var memory = provider.ForInternalAgent("core", "research", InternalAgentMemoryMode.CoreAndOwn);
        var recall = await memory.RecallAsync("q");
        CollectionAssert.AreEqual(new[] { o.Id, c.Id }, recall.WarmMemories.Select(e => e.Id).ToArray());
        await memory.ForgetMemoryAsync(c.Id);
        await own.Received(1).ForgetMemoryAsync(c.Id);
        await core.DidNotReceive().ForgetMemoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.AreSame(core, provider.ForInternalAgent("core", "research", InternalAgentMemoryMode.CoreOnly));
        Assert.AreSame(own, provider.ForInternalAgent("core", "research", InternalAgentMemoryMode.OwnOnly));
    }

    [TestMethod]
    public async Task HarnessRecallsFreshBoundedContextAcrossInvocations()
    {
        var memory = Substitute.For<IAgentMemoryService>();
        memory.RecallAsync(Arg.Any<string>(), null, Arg.Any<CancellationToken>()).Returns(new MemoryRecallResult());
        memory.FormatRecallContext(Arg.Any<MemoryRecallResult>()).Returns(AgentMemoryService.MemoryContextStart + new string('x', 3000) + AgentMemoryService.MemoryContextEnd);
        var client = FakeChatClient.WithText("done");
        var options = new FabrCoreHarnessOptions { DisableTodoProvider = true, DisableAgentModeProvider = true, DisableOpenTelemetry = true };
        options.WithMemory(memory, maxContextCharacters: 512);
        var harness = new FabrCoreHarnessAgent(client, options);
        var session = await harness.CreateSessionAsync();
        await harness.RunAsync("first question", session);
        await harness.RunAsync("second question", session);
        await memory.Received(1).RecallAsync("first question", null, Arg.Any<CancellationToken>());
        await memory.Received(1).RecallAsync("second question", null, Arg.Any<CancellationToken>());
        var injected = client.ReceivedMessages.Last().Where(m => m.AuthorName == "agent-memory").Last();
        Assert.IsLessThanOrEqualTo(512, injected.Text.Length);
        Assert.EndsWith(AgentMemoryService.MemoryContextEnd, injected.Text);
    }

    [TestMethod]
    public async Task CompactionExtractionFailurePreservesHistory()
    {
        var (host, history) = History();
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ExtractMemoriesAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<MemoryEntry>>>(_ => throw new InvalidOperationException("write failed"));
        var service = new MemoryAwareCompactionService(NullLoggerFactory.Instance, new TestChatClientService(FakeChatClient.WithText("summary")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync(history, Config(), memory, new(), "default"));
        await host.DidNotReceive().ReplaceThreadMessagesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<StoredChatMessage>>());
    }

    [TestMethod]
    public async Task CompactionForcedSplitExtractsAndSkipsSystemMemory()
    {
        var (host, history) = History();
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ExtractMemoriesAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<MemoryEntry>());
        memory.GetMemoryIndexAsync(Arg.Any<CancellationToken>()).Returns(new MemoryIndex());
        var config = Config(100);
        var service = new MemoryAwareCompactionService(NullLoggerFactory.Instance, new TestChatClientService(FakeChatClient.WithText("continuation")));
        var result = await service.CompactAsync(history, config, memory, new(), "default");
        Assert.IsTrue(result.WasCompacted);
        await memory.Received(1).ExtractMemoriesAsync(Arg.Is<IList<ChatMessage>>(m => m.Count == 1 && m[0].Text.Contains("source")), Arg.Any<CancellationToken>());
        await host.Received(1).ReplaceThreadMessagesAsync("thread", Arg.Any<IEnumerable<StoredChatMessage>>());
    }

    [TestMethod]
    public async Task CompactionMissingSummaryClientPreservesHistory()
    {
        var (host, history) = History();
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ExtractMemoriesAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<MemoryEntry>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MemoryAwareCompactionService(NullLoggerFactory.Instance)
            .CompactAsync(history, Config(), memory, new(), "default"));
        await host.DidNotReceive().ReplaceThreadMessagesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<StoredChatMessage>>());
    }

    [TestMethod]
    public async Task CompactionRejectsSummaryThatExpandsHistory()
    {
        var (host, history) = History();
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ExtractMemoriesAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<MemoryEntry>());
        memory.GetMemoryIndexAsync(Arg.Any<CancellationToken>()).Returns(new MemoryIndex());
        var service = new MemoryAwareCompactionService(NullLoggerFactory.Instance,
            new TestChatClientService(FakeChatClient.WithText(new string('x', 20000))));
        var result = await service.CompactAsync(history, Config(), memory, new(), "default");
        Assert.IsFalse(result.WasCompacted);
        await host.DidNotReceive().ReplaceThreadMessagesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<StoredChatMessage>>());
    }

    [TestMethod]
    public async Task CompactionHandlerPropagatesCancellation()
    {
        var (host, history) = History();
        var memory = Substitute.For<IAgentMemoryService>();
        memory.ExtractMemoriesAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<MemoryEntry>>>(_ => throw new OperationCanceledException());
        var handler = new MemoryCompactionHandler(memory, new MemoryAwareCompactionService(NullLoggerFactory.Instance), new(), NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.CompactAsync(history, Config()));
        await host.DidNotReceive().ReplaceThreadMessagesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<StoredChatMessage>>());
    }

    private static CompactionConfig Config(int keepLastN = 2) => new() { Enabled = true, MaxContextTokens = 100, Threshold = 0.5, KeepLastN = keepLastN };
    private static (IFabrCoreAgentHost Host, FabrCoreChatHistoryProvider History) History()
    {
        var host = Substitute.For<IFabrCoreAgentHost>();
        var messages = Enumerable.Range(0, 4).Select(i => new StoredChatMessage {
            Role = i == 0 ? "system" : "user", AuthorName = i == 0 ? "agent-memory" : null,
            ContentsJson = JsonSerializer.Serialize<List<AIContent>>([new TextContent("source " + new string('x', 1000))], AgentAbstractionsJsonUtilities.DefaultOptions)
        }).ToList();
        host.GetThreadMessagesAsync("thread").Returns(messages);
        return (host, new FabrCoreChatHistoryProvider(host, "thread"));
    }
}

