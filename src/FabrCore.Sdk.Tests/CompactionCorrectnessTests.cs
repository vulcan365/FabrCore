#pragma warning disable MAAI001
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class CompactionCorrectnessTests
{
    private const string Handover = "Active intent: ship ticket ABC-427. Constraints: no SQL. Correction: region west, not east. Open items: validate deployment.";
    private static readonly CompactionConfig Config = new() { MaxContextTokens = 4000, Threshold = .75, KeepLastN = 2 };

    private static StoredChatMessage Message(string role, params AIContent[] contents) => new()
    {
        Role = role, ContentsJson = JsonSerializer.Serialize(contents.ToList(), AgentAbstractionsJsonUtilities.DefaultOptions)
    };

    private static List<StoredChatMessage> History() =>
    [
        Message("system", new TextContent("Never use SQL; exact ticket ABC-427.")),
        Message("user", new TextContent("Original request " + new string('a', 7000))),
        Message("assistant", new TextContent("Investigated " + new string('b', 7000))),
        Message("user", new TextContent("Correction: region west, not east. Continue ABC-427.")),
        Message("assistant", new TextContent("Deployment validation remains open."))
    ];

    private static (CompactionService Service, FabrCoreChatHistoryProvider Provider, FakeAgentHost Host) Setup(
        FakeChatClient client, int? window = 128000)
    {
        var host = new FakeAgentHost("test:agent");
        host.Threads["main"] = History();
        var service = new FakeChatClientService(client)
        {
            ModelConfiguration = new()
            { Name = "summary", Provider = "Test", Uri = "http://localhost", Model = "test", ApiKeyAlias = "test", ContextWindowTokens = window, MaxOutputTokens = 1536 }
        };
        return (new CompactionService(service, NullLogger<CompactionService>.Instance), new(host, "main"), host);
    }

    [TestMethod]
    public async Task DurableCompactionCommitsOnceAndPreservesConstraintsAndCorrection()
    {
        var client = FakeChatClient.WithTextResponse(Handover);
        var (service, provider, host) = Setup(client);
        var original = host.Threads["main"].ToList();
        var result = await service.CompactIfNeededAsync(provider, Config, "summary");
        Assert.IsTrue(result.WasCompacted);
        Assert.AreEqual(1, host.HistoryReplacements);
        Assert.IsTrue(result.EstimatedTokensAfter <= 3000);
        Assert.AreEqual(original[0].ContentsJson, host.Threads["main"][0].ContentsJson);
        Assert.IsTrue(host.Threads["main"].Any(m => m.Id == original[3].Id && m.ContentsJson == original[3].ContentsJson));
        var summary = host.Threads["main"].Single(m => m.AuthorName == "compaction");
        Assert.AreEqual("assistant", summary.Role);
        StringAssert.Contains(summary.ContentsJson, "ABC-427");
        Assert.IsFalse((await service.CompactIfNeededAsync(provider, Config, "summary")).WasCompacted);
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("length")]
    [DataRow("oversize")]
    [DataRow("cancel")]
    [DataRow("unknown-model")]
    [DataRow("atomic-group-too-large")]
    [DataRow("write-failure")]
    public async Task FailureLeavesDurableHistoryAndCacheIntact(string failure)
    {
        var response = FakeChatClient.Text(failure == "empty" ? " " : failure == "oversize" ? new string('x', 20000) : Handover);
        if (failure == "length") response.FinishReason = ChatFinishReason.Length;
        var client = failure == "cancel" ? FakeChatClient.Canceled() : FakeChatClient.Scripted(response);
        var (service, provider, host) = Setup(client, failure == "unknown-model" ? null : failure == "atomic-group-too-large" ? 4096 : 128000);
        var original = host.Threads["main"].Select(m => m.ContentsJson).ToArray();
        await provider.GetMessagesAsync();
        host.FailHistoryReplacement = failure == "write-failure";
        await Assert.ThrowsAsync<Exception>(() => service.CompactIfNeededAsync(provider, Config, "summary"));
        CollectionAssert.AreEqual(original, host.Threads["main"].Select(m => m.ContentsJson).ToArray());
        Assert.AreEqual(0, host.HistoryReplacements);
        Assert.IsTrue((await provider.GetMessagesAsync()).Any(m => m.Text.Contains("Original request")));
    }

    [TestMethod]
    public async Task ConcurrentHistoryChangeAbandonsReplacement()
    {
        var (service, provider, host) = Setup(FakeChatClient.WithTextResponse(Handover));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.CompactIfNeededAsync(provider, Config, "summary",
            () => host.AddThreadMessagesAsync("main", [Message("user", new TextContent("New request during compaction"))])));
        Assert.AreEqual(0, host.HistoryReplacements);
        Assert.AreEqual(6, host.Threads["main"].Count);
    }

    [TestMethod]
    public async Task SummaryInputRetainsMixedContentAndParallelToolPairs()
    {
        var client = FakeChatClient.WithTextResponse(Handover);
        var (service, provider, host) = Setup(client);
        host.Threads["main"].InsertRange(2,
        [
            Message("assistant", new TextContent("Checking inventory"), new FunctionCallContent("call-A", "lookup", new Dictionary<string, object?> { ["id"] = "ABC-427" }), new FunctionCallContent("call-B", "lookup")),
            Message("tool", new TextContent("Mixed result"), new FunctionResultContent("call-B", new { region = "west" }), new FunctionResultContent("call-A", new { count = 42 }))
        ]);
        await service.CompactIfNeededAsync(provider, Config, "summary");
        var input = string.Join("\n", client.Requests.SelectMany(r => r).Select(m => m.Text));
        foreach (var fact in new[] { "Checking inventory", "Mixed result", "call-A", "call-B", "west", "42" }) StringAssert.Contains(input, fact);
    }

    [TestMethod]
    public void ToolProjectionPreservesIdsAndStructuredDataWithoutClippingUserText()
    {
        var original = Message("tool", new FunctionResultContent("call-A", new { start = "ABC-427", body = new string('x', 10000), end = "west" }));
        var projected = CompactionService.TruncateSingleMessage(original, 300);
        Assert.AreEqual(original.Id, projected.Id);
        StringAssert.Contains(projected.ContentsJson, "ABC-427");
        StringAssert.Contains(projected.ContentsJson, "west");
        Assert.IsTrue(projected.ContentsJson.Length < original.ContentsJson.Length);
        var user = Message("user", new TextContent(new string('x', 10000)));
        Assert.AreSame(user, CompactionService.TruncateSingleMessage(user, 100));
    }

    [TestMethod]
    public async Task InRunCompactionRetainsActiveRequestAndCompleteToolProtocol()
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "No SQL"), new(ChatRole.User, "Continue ABC-427 in west; preserve this request.") };
        for (var i = 0; i < 12; i++)
        {
            messages.Add(new(ChatRole.Assistant, [new FunctionCallContent($"call-{i}", "lookup")]));
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent($"call-{i}", new { start = "ABC-427", body = new string('x', 8000), end = "west" })]));
        }
        var index = new CompactionMessageIndex([]);
        index.AddGroup(CompactionGroupKind.System, [messages[0]]);
        index.AddGroup(CompactionGroupKind.User, [messages[1]], 1);
        for (var i = 2; i < messages.Count; i += 2)
            index.AddGroup(CompactionGroupKind.ToolCall, [messages[i], messages[i + 1]], 1);
        var strategy = new ContextCompaction.ProtectedToolCompactionStrategy(new()
        { MaxContextWindowTokens = 128000, MaxOutputTokens = 8000, WorkingSetTokens = 4000 });
        Assert.IsTrue(await strategy.CompactAsync(index));
        var included = index.GetIncludedMessages().ToList();
        Assert.AreSame(messages[1], included.Single(m => m.Role == ChatRole.User));
        var calls = included.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToArray();
        var results = included.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId).ToArray();
        CollectionAssert.AreEquivalent(calls, results);
        Assert.IsTrue(ChatRunSafetyScope.EstimateTokens(included) < ChatRunSafetyScope.EstimateTokens(messages));
        Assert.IsFalse(await strategy.CompactAsync(index), "Repeated compaction must settle without growing summaries.");
        Assert.AreEqual(8000, ((JsonElement)JsonSerializer.SerializeToElement(((FunctionResultContent)messages[^1].Contents[0]).Result)).GetProperty("body").GetString()!.Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PhysicalGuardIncludesOutputReserveWithoutRunScope(bool streaming)
    {
        var client = FakeChatClient.WithTextResponse("Must not run");
        var tracked = new TokenTrackingChatClient(client) { ContextWindowTokens = 1000, ReservedOutputTokens = 800 };
        async Task Run()
        {
            if (streaming) { await foreach (var _ in tracked.GetStreamingResponseAsync([new(ChatRole.User, new string('x', 2000))])) { } }
            else await tracked.GetResponseAsync([new(ChatRole.User, new string('x', 2000))]);
        }
        await Assert.ThrowsExactlyAsync<FabrCoreRunStoppedException>(Run);
        Assert.AreEqual(0, client.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MalformedToolProtocolCannotBeCommitted(bool unfinished)
    {
        var (service, provider, host) = Setup(FakeChatClient.WithTextResponse(Handover));
        host.Threads["main"].Insert(2, unfinished
            ? Message("assistant", new FunctionCallContent("missing-result", "lookup"))
            : Message("tool", new FunctionResultContent("missing-call", "result")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.CompactIfNeededAsync(provider, Config, "summary"));
        Assert.AreEqual(0, host.HistoryReplacements);
    }

    [TestMethod]
    public async Task RepeatedDurableAndInRunCompactionSurvivesSessionRestore()
    {
        var summarizer = FakeChatClient.WithTextResponse(Handover);
        var (service, provider, host) = Setup(summarizer);
        var client = FakeChatClient.WithTextResponse("Validation is still pending.");
        var agent = new FabrCoreHarnessAgent(client, new()
        {
            DisableTodoProvider = true, DisableAgentModeProvider = true, ChatHistoryProvider = provider,
            AIContextProviders = [ContextCompaction.TryCreateProvider(new()
            { MaxContextWindowTokens = 128000, MaxOutputTokens = 8000, WorkingSetTokens = 4000 })!]
        });
        var session = await agent.CreateSessionAsync();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            if (cycle > 0) host.Threads["main"].Insert(1, Message("assistant", new TextContent("OLD-PAYLOAD " + new string('z', 16000))));
            Assert.IsTrue((await service.CompactIfNeededAsync(provider, Config, "summary")).WasCompacted);
            await agent.RunAsync("Continue ABC-427 in west, no SQL.", session);
            await provider.FlushAsync();
            var prompt = client.PromptAt(cycle);
            foreach (var fact in new[] { "ABC-427", "west", "SQL", "validate deployment" }) StringAssert.Contains(prompt, fact);
            Assert.IsFalse(prompt.Contains("OLD-PAYLOAD"));
            var snapshot = ContextCompaction.StripSessionState(await agent.SerializeSessionAsync(session));
            session = await agent.DeserializeSessionAsync(snapshot);
        }
        Assert.AreEqual(3, host.HistoryReplacements);
        Assert.IsTrue(summarizer.Requests[1].Any(m => m.Text.Contains(Handover)), "Repeated summaries must consume the prior handover.");
    }

    [TestMethod]
    public async Task SameLengthHistoryRewriteDoesNotReuseStalePrefix()
    {
        var client = FakeChatClient.WithTextResponse("Same final answer");
        var host = new FakeAgentHost("test:agent");
        var provider = new FabrCoreChatHistoryProvider(host, "main");
        var agent = new FabrCoreHarnessAgent(client, new()
        {
            DisableTodoProvider = true, DisableAgentModeProvider = true, ChatHistoryProvider = provider,
            AIContextProviders = [ContextCompaction.TryCreateProvider(new()
            { MaxContextWindowTokens = 128000, MaxOutputTokens = 8000 })!]
        });
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("Obsolete region east", session);
        await provider.FlushAsync();
        var replacement = host.Threads["main"].ToList();
        replacement[0] = Message("user", new TextContent("Corrected region west"));
        await provider.ReplaceAndResetCacheAsync(replacement);
        await agent.RunAsync("Continue", session);
        StringAssert.Contains(client.PromptAt(1), "Corrected region west");
        Assert.IsFalse(client.PromptAt(1).Contains("Obsolete region east"));
    }

    [TestMethod]
    public async Task OversizedProtectedProjectionStopsRatherThanSilentlyDroppingTask()
    {
        var client = FakeChatClient.WithTextResponse("Must not run");
        var host = new FakeAgentHost("test:agent");
        host.Threads["main"] = [Message("user", new TextContent(new string('a', 10000)))];
        var provider = new FabrCoreChatHistoryProvider(host, "main")
        { ActiveProjection = new() { MaxContextTokens = 1000 } };
        var agent = new FabrCoreHarnessAgent(client, new()
        { DisableTodoProvider = true, DisableAgentModeProvider = true, ChatHistoryProvider = provider });
        await Assert.ThrowsExactlyAsync<FabrCoreRunStoppedException>(() => agent.RunAsync("Continue", null));
        Assert.AreEqual(0, client.CallCount);
        Assert.IsTrue(host.Threads["main"][0].ContentsJson.Length > 10000);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChunkReductionIsBoundedAndRejectsNoProgress(bool noProgress)
    {
        var client = FakeChatClient.WithTextResponse(noProgress ? new string('z', 3000) : Handover);
        var (service, provider, host) = Setup(client, 6000);
        host.Threads["main"] = Enumerable.Range(0, 10)
            .Select(i => Message("assistant", new TextContent($"Entry {i}: " + new string('x', 1500))))
            .Append(Message("user", new TextContent("Continue ABC-427"))).ToList();
        var config = Config with { MaxContextTokens = 2000, SummaryModelConfigurationName = "smaller-summarizer" };
        if (noProgress)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.CompactIfNeededAsync(provider, config, "agent-model"));
            Assert.AreEqual(0, host.HistoryReplacements);
        }
        else Assert.IsTrue((await service.CompactIfNeededAsync(provider, config, "agent-model")).WasCompacted);
        Assert.IsTrue(client.CallCount is > 1 and <= 12);
        for (var i = 0; i < client.CallCount; i++)
        {
            var textBytes = client.Requests[i].Sum(m => System.Text.Encoding.UTF8.GetByteCount(m.Text));
            Assert.IsTrue(textBytes + client.RequestOptions[i]!.MaxOutputTokens <= 6000,
                "Every summarizer request, including reduction, must respect the configured model budget.");
        }
    }

    [TestMethod]
    public async Task InRunExcerptsDoNotReplaceOriginalStoredToolResults()
    {
        var client = FakeChatClient.Scripted(Enumerable.Range(0, 8)
            .Select(i => FakeChatClient.ToolCall($"call-{i}", "lookup", "{}"))
            .Append(FakeChatClient.Text("Finished")).ToArray());
        var host = new FakeAgentHost("test:agent");
        var provider = new FabrCoreChatHistoryProvider(host, "main");
        var agent = new FabrCoreHarnessAgent(client, new()
        {
            DisableTodoProvider = true, DisableAgentModeProvider = true, ChatHistoryProvider = provider,
            ChatOptions = new() { Tools = [AIFunctionFactory.Create(() => new string('x', 8000), "lookup")] },
            AIContextProviders = [ContextCompaction.TryCreateProvider(new()
            { MaxContextWindowTokens = 128000, MaxOutputTokens = 8000, WorkingSetTokens = 4000 })!]
        });
        await agent.RunAsync("Look up records", await agent.CreateSessionAsync());
        await provider.FlushAsync();
        var results = host.Threads["main"].SelectMany(CompactionTranscript.Contents).OfType<FunctionResultContent>().ToList();
        Assert.AreEqual(8, results.Count);
        Assert.IsTrue(results.All(r => CompactionTranscript.ResultText(r.Result).Contains(new string('x', 8000))));
        Assert.IsTrue(client.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Any(r => CompactionTranscript.ResultText(r.Result).Contains("middle omitted")));
    }
}
