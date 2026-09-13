#pragma warning disable MAAI001
using System.Text.Json;
using System.Runtime.CompilerServices;
using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class HarnessEfficiencyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompactionSeesEveryModelCallButRecallRunsOnce(bool streaming)
    {
        var client = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("one", "fetch", "{}"),
            FakeChatClient.ToolCall("two", "fetch", "{}"),
            FakeChatClient.Text("Finished"));
        var strategy = new ObservingStrategy();
        var recall = new CountingRecall();
        var agent = new FabrCoreHarnessAgent(client, new FabrCoreHarnessOptions
        {
            DisableTodoProvider = true, DisableAgentModeProvider = true,
            AIContextProviders = [recall, new CompactionProvider(strategy, ContextCompaction.StateKey)],
            ChatOptions = new() { Tools = [AIFunctionFactory.Create(() => new string('x', 4000), "fetch")] }
        });
        var session = await agent.CreateSessionAsync();
        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync("Fetch twice", session)) { }
        }
        else
            await agent.RunAsync("Fetch twice", session);

        Assert.AreEqual(3, client.CallCount);
        Assert.AreEqual(3, strategy.Calls);
        Assert.AreEqual(1, recall.Calls);
        Assert.IsTrue(strategy.ToolResults >= 2, "Compaction must see tool results added within this invocation.");
        Assert.AreEqual(1, client.Requests[^1].Count(m => m.AuthorName == "recall"));
    }

    [TestMethod]
    public async Task WorkingSetCompactionReducesRepeatedToolPayloads()
    {
        async Task<long> Run(bool compact)
        {
            var responses = Enumerable.Range(0, 12)
                .Select(i => FakeChatClient.ToolCall($"call{i}", "fetch", "{}"))
                .Append(FakeChatClient.Text("Finished")).ToArray();
            var client = FakeChatClient.Scripted(responses);
            var provider = ContextCompaction.TryCreateProvider(new()
            {
                MaxContextWindowTokens = 100_000, MaxOutputTokens = 8192, WorkingSetTokens = 4000
            })!;
            var agent = new FabrCoreHarnessAgent(client, new()
            {
                DisableTodoProvider = true, DisableAgentModeProvider = true,
                AIContextProviders = compact ? [provider] : null,
                ChatOptions = new() { Tools = [AIFunctionFactory.Create(() => new string('x', 8000), "fetch")] }
            });
            await agent.RunAsync("Fetch records", await agent.CreateSessionAsync());
            Assert.AreEqual(13, client.CallCount);
            return client.Requests.Sum(ChatRunSafetyScope.EstimateTokens);
        }
        var baseline = await Run(false);
        var compacted = await Run(true);
        Assert.IsTrue(compacted < baseline, $"Expected lower repeated input: {compacted} vs {baseline} estimated tokens.");
    }

    [TestMethod]
    public async Task InstructionsAndToolSchemasAreCheckedBeforeSending()
    {
        var client = FakeChatClient.WithTextResponse("Should not be called");
        using var safety = ChatRunSafetyScope.Begin("owner:agent", null, null,
            new() { MaxPromptInputTokens = 100 }, null, null);
        var tracked = new TokenTrackingChatClient(client);
        var options = new ChatOptions
        {
            Instructions = new string('s', 800),
            Tools = [AIFunctionFactory.Create((string query) => query, "search")]
        };
        await Assert.ThrowsExactlyAsync<FabrCoreRunStoppedException>(() =>
            tracked.GetResponseAsync([new(ChatRole.User, "Hi")], options));
        Assert.AreEqual(0, client.CallCount);
        Assert.IsTrue(ChatRunSafetyScope.EstimateTokens([], new() { Tools = options.Tools }) > 10);
    }

    [TestMethod]
    public void StructuredToolResultsUseSerializedContent()
    {
        var result = new Dictionary<string, object> { ["records"] = new string('x', 4000) };
        var estimate = ChatRunSafetyScope.EstimateTokens([new(ChatRole.Tool, [new FunctionResultContent("one", result)])]);
        Assert.IsTrue(estimate >= 1000);
    }

    [TestMethod]
    public void WorkingSetIsOptionalAndCannotExceedPhysicalInputBudget()
    {
        var config = new ContextCompactionConfig { MaxContextWindowTokens = 100_000, MaxOutputTokens = 8192 };
        Assert.AreEqual(91808, config.InputBudgetTokens);
        Assert.AreEqual(16000, (config with { WorkingSetTokens = 16000 }).InputBudgetTokens);
        Assert.AreEqual(91808, (config with { WorkingSetTokens = 200_000 }).InputBudgetTokens);
        Assert.IsFalse((config with { WorkingSetTokens = 0 }).IsUsable);
    }

    [TestMethod]
    public async Task RewrittenHistoryDoesNotReappearFromTheLiveCompactionIndex()
    {
        var client = FakeChatClient.WithTextResponse("Reply");
        var history = new FabrCoreChatHistoryProvider(new FakeAgentHost("owner:agent"), "main");
        var agent = new FabrCoreHarnessAgent(client, new()
        {
            DisableTodoProvider = true, DisableAgentModeProvider = true, ChatHistoryProvider = history,
            AIContextProviders = [ContextCompaction.TryCreateProvider(new()
            {
                MaxContextWindowTokens = 100_000, MaxOutputTokens = 8192
            })!]
        });
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("Old detailed history", session);
        await history.ReplaceAndResetCacheAsync([new StoredChatMessage
        {
            Role = "user", ContentsJson = JsonSerializer.Serialize<List<AIContent>>(
                [new TextContent("Replacement summary")], AgentAbstractionsJsonUtilities.DefaultOptions)
        }]);
        await agent.RunAsync("Next question", session);
        Assert.IsTrue(client.PromptAt(1).Contains("Replacement summary"));
        Assert.IsFalse(client.PromptAt(1).Contains("Old detailed history"));

        var snapshot = ContextCompaction.StripSessionState(await agent.SerializeSessionAsync(session));
        Assert.IsFalse(snapshot.GetRawText().Contains(ContextCompaction.StateKey));
        var restored = await agent.DeserializeSessionAsync(snapshot);
        await agent.RunAsync("After restore", restored);
        Assert.IsFalse(client.PromptAt(2).Contains("Old detailed history"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearingOrDisposingCancelsLocalBackgroundWork(bool dispose)
    {
        var worker = new BlockingAgent();
        var client = FakeChatClient.Scripted(
            FakeChatClient.ToolCall("start", "background_agents_start_task",
                """{"agentName":"worker","input":"work","description":"Work"}"""),
            FakeChatClient.Text("Started"));
        var agent = new FabrCoreHarnessAgent(client, new()
        {
            DisableTodoProvider = true, DisableAgentModeProvider = true, BackgroundAgents = [worker]
        });
        var result = new FabrCoreHarnessResult(agent, await agent.CreateSessionAsync(), "main", "owner:agent");
        await result.RunAsync("Start work");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var previous = result.Session;
        if (dispose) await result.DisposeAsync();
        else await result.ClearHarnessSessionAsync();
        await worker.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, agent.BackgroundAgents!.GetIncompleteTasks(previous).Count);
        if (!dispose) Assert.AreNotSame(previous, result.Session);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoopUsageIncludesEveryCallWithoutDoubleCounting(bool streaming)
    {
        var first = FakeChatClient.Text("Continue");
        first.Usage = new() { InputTokenCount = 100, OutputTokenCount = 10, CachedInputTokenCount = 20 };
        var last = FakeChatClient.Text("DONE");
        last.Usage = new() { InputTokenCount = 200, OutputTokenCount = 20, CachedInputTokenCount = 50 };
        var client = FakeChatClient.Scripted(first, last);
        var agent = new FabrCoreHarnessAgent(client, new()
        {
            DisableTodoProvider = true, DisableAgentModeProvider = true,
            LoopMode = HarnessLoopMode.Marker, LoopCompletionMarker = "DONE"
        });
        UsageDetails usage;
        var session = await agent.CreateSessionAsync();
        if (streaming)
        {
            usage = new() { InputTokenCount = 0, OutputTokenCount = 0, CachedInputTokenCount = 0 };
            await foreach (var update in agent.RunStreamingAsync("Work", session))
                foreach (var content in update.Contents.OfType<UsageContent>())
                {
                    usage.InputTokenCount += content.Details.InputTokenCount ?? 0;
                    usage.OutputTokenCount += content.Details.OutputTokenCount ?? 0;
                    usage.CachedInputTokenCount += content.Details.CachedInputTokenCount ?? 0;
                }
        }
        else
        {
            var response = await agent.RunAsync("Work", session);
            Assert.AreEqual("DONE", response.Text);
            usage = response.Usage!;
        }
        Assert.AreEqual(300L, usage.InputTokenCount);
        Assert.AreEqual(30L, usage.OutputTokenCount);
        Assert.AreEqual(70L, usage.CachedInputTokenCount);
        Assert.AreEqual(2, client.CallCount);
    }

    private sealed class ObservingStrategy() : CompactionStrategy(_ => true)
    {
        public int Calls;
        public int ToolResults;
        protected override ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
        {
            Calls++;
            ToolResults = Math.Max(ToolResults, index.GetIncludedMessages().SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count());
            return ValueTask.FromResult(false);
        }
    }

    private sealed class CountingRecall : AIContextProvider
    {
        public int Calls;
        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new AIContext { Messages = [new(ChatRole.User, "Recalled fact") { AuthorName = "recall" }] });
        }
    }

    private sealed class BlockingAgent : AIAgent
    {
        public override string Name => "worker";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentSession>(new FabrCoreBackgroundAgentSession());
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => CreateSessionCoreAsync(cancellationToken);
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null, AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await RunCoreAsync(messages, session, options, cancellationToken);
            yield break;
        }
        protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            return new AgentResponse([]);
        }
    }
}
