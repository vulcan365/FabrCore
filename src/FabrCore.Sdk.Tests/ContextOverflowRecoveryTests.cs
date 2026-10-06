using System.Runtime.CompilerServices;
using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Sdk.Tests;

/// <summary>
/// A provider rejecting a prompt as larger than the model's context is recognized, teaches the host a
/// limit, and is recovered by trimming tool output and retrying once.
/// </summary>
[TestClass]
public sealed class ContextOverflowRecoveryTests
{
    private const string OpenAiChat =
        """{"error":{"message":"This model's maximum context length is 128000 tokens. However, your messages resulted in 130532 tokens. Please reduce the length of the messages.","type":"invalid_request_error","param":"messages","code":"context_length_exceeded"}}""";
    private const string OpenAiResponses =
        """{"error":{"message":"Your input exceeds the context window of this model. Please adjust your input and try again.","type":"invalid_request_error","param":"input","code":"context_length_exceeded"}}""";
    private const string SmallWindow =
        """{"error":{"message":"This model's maximum context length is 8000 tokens. However, your messages resulted in 9000 tokens.","type":"invalid_request_error","param":"messages","code":"context_length_exceeded"}}""";

    // ── Recognizing a rejection ──

    [TestMethod]
    [DataRow("OpenAI", null, false)]
    [DataRow("OpenAI", null, true)]
    [DataRow("Azure", null, false)]
    [DataRow("Azure", null, true)]
    [DataRow("OpenAI", "Responses", false)]
    [DataRow("OpenAI", "Responses", true)]
    [DataRow("Azure", "Responses", false)]
    [DataRow("Azure", "Responses", true)]
    public async Task RealSdkPipeline_SurfacesOverflowAsClassifiableException(string provider, string? chatApi, bool streaming)
    {
        var body = chatApi is null ? OpenAiChat : OpenAiResponses;
        var failure = await ScriptedProvider.FailureAsync(400, body, provider, chatApi, streaming);

        Assert.IsTrue(ContextOverflow.TryClassify(failure, out var overflow), failure.Message);
        Assert.AreEqual(chatApi is null ? 128_000 : null, overflow.LimitTokens);
        Assert.AreEqual(chatApi is null ? 130_532L : null, overflow.RequestTokens);
    }

    [TestMethod]
    // OpenAI-compatible providers the SDK cannot parse: the wording survives only in the raw body.
    [DataRow("""{"error":"prompt is too long: 250000 tokens > 200000 maximum"}""", 200_000, 250_000L)]
    [DataRow("""[{"error":{"code":400,"message":"The input token count (1200000) exceeds the maximum number of tokens allowed (1048576).","status":"INVALID_ARGUMENT"}}]""", 1_048_576, 1_200_000L)]
    [DataRow("""{"error":{"code":400,"message":"This endpoint's maximum context length is 131072 tokens. However, you requested about 140000 tokens.","metadata":{}}}""", 131_072, 140_000L)]
    [DataRow("""{"error":{"message":"This model's maximum prompt length is 131072 but the request contains 140000 tokens.","code":"invalid_request"}}""", 131_072, 140_000L)]
    [DataRow("""{"error":{"message":"Input tokens exceed the configured limit of 272000 tokens. Your messages resulted in 300000 tokens.","code":"invalid_request_error"}}""", 272_000, 300_000L)]
    public async Task Classify_RecognizesKnownProviderBodies(string body, int limit, long request)
    {
        var failure = await ScriptedProvider.FailureAsync(400, body);

        Assert.IsTrue(ContextOverflow.TryClassify(failure, out var overflow), failure.Message);
        Assert.AreEqual(limit, overflow.LimitTokens);
        Assert.AreEqual(request, overflow.RequestTokens);
    }

    [TestMethod]
    // A rate limit that happens to talk about request size.
    [DataRow(429, """{"error":{"message":"Request too large for gpt-test: Limit 30000, Requested 40000. The input or output tokens must be reduced.","type":"tokens","param":null,"code":"rate_limit_exceeded"}}""")]
    // The output request is too big, not the prompt.
    [DataRow(400, """{"error":{"message":"max_tokens is too large: 50000. This model supports at most 16384 completion tokens, whereas you provided 50000.","type":"invalid_request_error","param":"max_tokens","code":"invalid_value"}}""")]
    [DataRow(400, """{"error":{"message":"This model's maximum context length is 4097 tokens.","type":"invalid_request_error","param":"max_completion_tokens","code":"context_length_exceeded"}}""")]
    [DataRow(400, """{"error":{"message":"The response was filtered due to the prompt triggering the content management policy.","type":null,"param":"prompt","code":"content_filter"}}""")]
    [DataRow(400, """{"error":{"message":"Bad request","type":"invalid_request_error","param":null,"code":null}}""")]
    // The right words on the wrong status are not a prompt rejection.
    [DataRow(401, OpenAiChat)]
    [DataRow(404, OpenAiChat)]
    [DataRow(500, OpenAiChat)]
    public async Task Classify_RejectsLookalikes(int status, string body)
    {
        var failure = await ScriptedProvider.FailureAsync(status, body);

        Assert.IsFalse(ContextOverflow.TryClassify(failure, out _), failure.Message);
    }

    [TestMethod]
    public async Task Classify_RejectsWhatItCannotRead()
    {
        var html = await ScriptedProvider.FailureAsync(413, "<html>Request Entity Too Large</html>", contentType: "text/html");
        Assert.IsFalse(ContextOverflow.TryClassify(html, out _));

        Assert.IsFalse(
            ContextOverflow.TryClassify(new InvalidOperationException("maximum context length is 128000 tokens"), out _),
            "Only a provider's own rejection counts.");
    }

    [TestMethod]
    public async Task RealSdkPipeline_ReportsResponsesFailuresInBand()
    {
        const string created = "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"in_progress\",\"model\":\"gpt-test\",\"output\":[]}}\n\n";
        const string error = "event: error\ndata: {\"type\":\"error\",\"sequence_number\":1,\"code\":\"context_length_exceeded\",\"message\":\"Your input exceeds the context window of this model.\",\"param\":\"input\"}\n\n";
        const string failed = "event: response.failed\ndata: {\"type\":\"response.failed\",\"sequence_number\":1,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"failed\",\"model\":\"gpt-test\",\"output\":[],\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"Your input exceeds the context window of this model.\"}}}\n\n";

        foreach (var stream in new[] { created + error, created + failed })
        {
            var client = ScriptedProvider.Client("OpenAI", "Responses", new ScriptedProvider.Reply(200, stream, "text/event-stream"));
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync("hello")) updates.Add(update);

            Assert.AreEqual(1, updates.Count(update => ContextOverflow.TryClassify(update, out _)));
            Assert.IsFalse(ContextOverflow.TryClassify(updates[0], out _), "The opening update carries no error.");
        }

        var completed = ScriptedProvider.Client("OpenAI", "Responses", new ScriptedProvider.Reply(200,
            """{"id":"resp_1","object":"response","created_at":1,"status":"failed","model":"gpt-test","output":[],"error":{"code":"context_length_exceeded","message":"Your input exceeds the context window of this model."}}"""));
        Assert.IsTrue(ContextOverflow.TryClassify(await completed.GetResponseAsync("hello"), out _));
    }

    // ── What a rejection teaches ──

    [TestMethod]
    public void StatedLimit_CapsButNeverRaisesTheConfiguredWindow()
    {
        var limits = Limits(window: 200_000, output: 16_000);

        Assert.IsFalse(limits.Record(new(300_000, null, ""), promptEstimate: 150_000), "A limit above the window is not a cap.");
        Assert.IsFalse(limits.Record(new(1_000, null, ""), promptEstimate: 150_000), "An implausibly small limit is ignored.");
        Assert.IsNull(limits.StatedLimit);
        Assert.IsFalse(limits.Effective(forTrim: false).WindowIsLearned);

        Assert.IsTrue(limits.Record(new(128_000, null, ""), promptEstimate: 150_000));
        var capped = limits.Effective(forTrim: false);
        Assert.AreEqual(128_000, capped.MaxContextWindowTokens);
        Assert.AreEqual(16_000, capped.MaxOutputTokens, "A stated output limit is kept as it is.");
        Assert.IsTrue(capped.WindowIsLearned);

        limits.Record(new(100_000, null, ""), promptEstimate: 110_000);
        limits.Record(new(128_000, null, ""), promptEstimate: 110_000);
        Assert.AreEqual(100_000, limits.StatedLimit, "The lowest stated limit stands.");
    }

    [TestMethod]
    public void StatedLimit_RederivesADerivedOutputReserve()
    {
        var limits = Limits(window: 200_000);
        Assert.AreEqual(25_000, limits.Configured.MaxOutputTokens);

        limits.Record(new(128_000, null, ""), promptEstimate: 150_000);

        var capped = limits.Effective(forTrim: false);
        Assert.AreEqual(16_000, capped.MaxOutputTokens);
        Assert.IsTrue(capped.OutputReserveIsDerived);
    }

    [TestMethod]
    public void StatedLimit_IsDroppedWhenTheModelConfigurationChanges()
    {
        var registry = new ModelContextLimitRegistry();
        var limits = Limits(window: 200_000, registry: registry);
        limits.Record(new(128_000, null, ""), promptEstimate: 150_000);
        Assert.AreEqual(128_000, limits.StatedLimit);

        // The operator corrects the window: what was learned against the old value no longer applies.
        Configure(limits, window: 150_000);

        Assert.IsNull(limits.StatedLimit);
        Assert.AreEqual(150_000, limits.Effective(forTrim: true).MaxContextWindowTokens);
        Assert.IsNull(Limits(window: 150_000, registry: registry).StatedLimit);
    }

    [TestMethod]
    public void StatedLimit_ExpiresAfterTtl()
    {
        var clock = new ManualTimeProvider();
        var limits = Limits(window: 200_000, registry: new ModelContextLimitRegistry(clock));
        limits.Record(new(128_000, null, ""), promptEstimate: 150_000);

        clock.Advance(ModelContextLimitRegistry.Lifetime - TimeSpan.FromMinutes(1));
        Assert.AreEqual(128_000, limits.StatedLimit);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsNull(limits.StatedLimit);
        Assert.AreEqual(200_000, limits.Effective(forTrim: false).MaxContextWindowTokens);
    }

    [TestMethod]
    public void StatedLimit_WithoutConfiguredWindow_DoesNotEnableLayerOne()
    {
        var limits = Limits(window: null);

        Assert.IsFalse(limits.Record(new(8_000, 9_000, ""), promptEstimate: 9_000));

        Assert.IsFalse(limits.Effective(forTrim: true).IsUsable, "A learned limit never stands in for a missing window.");
        Assert.AreEqual(0, limits.Effective(forTrim: false).MaxContextWindowTokens);
    }

    [TestMethod]
    public void EstimateCeiling_TightensTrimmingOnlyAndStaysInsideTheActivation()
    {
        var registry = new ModelContextLimitRegistry();
        var first = Limits(window: 128_000, registry: registry);
        var second = Limits(window: 128_000, registry: registry);

        // Refused at an estimate that passed the local guard, with no limit stated: the estimate
        // undercounts this conversation, so trim against where it failed.
        first.Record(new(null, null, ""), promptEstimate: 100_000);

        Assert.AreEqual(100_000, first.Effective(forTrim: true).MaxContextWindowTokens);
        Assert.AreEqual(128_000, first.Effective(forTrim: false).MaxContextWindowTokens, "A guess never moves a hard stop.");
        Assert.AreEqual(128_000, second.Effective(forTrim: true).MaxContextWindowTokens, "Another agent's content is not this one's.");

        // The provider's numbers scale its limit into estimate units: 90,000 × 128,000 / 144,000.
        first.Record(new(128_000, 144_000, ""), promptEstimate: 90_000);
        Assert.AreEqual(80_000, first.Effective(forTrim: true).MaxContextWindowTokens);

        // One wild rejection cannot collapse the working set.
        first.Record(new(null, null, ""), promptEstimate: 1_000);
        Assert.AreEqual(64_000, first.Effective(forTrim: true).MaxContextWindowTokens);
    }

    [TestMethod]
    public void Registry_ResolvesFromTheContainerAndUsesItsClock()
    {
        // The host registers the registry by type; a registered TimeProvider (Orleans supplies one) is honoured.
        var clock = new ManualTimeProvider();
        using var withClock = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<ModelContextLimitRegistry>()
            .BuildServiceProvider();
        var registry = withClock.GetRequiredService<ModelContextLimitRegistry>();
        registry.Record("default", "fingerprint", 128_000);
        clock.Advance(ModelContextLimitRegistry.Lifetime + TimeSpan.FromMinutes(1));
        Assert.IsNull(registry.Get("default", "fingerprint"));

        using var withoutClock = new ServiceCollection().AddSingleton<ModelContextLimitRegistry>().BuildServiceProvider();
        Assert.IsNotNull(withoutClock.GetRequiredService<ModelContextLimitRegistry>());
    }

    // ── Recovering the call ──

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Overflow_TrimsOlderToolOutputAndRetriesOnce(bool streaming)
    {
        var fake = FakeChatClient.WithTextResponse("Recovered").FailAt(0, await ScriptedProvider.FailureAsync(400, OpenAiResponses));
        var client = Recovery(fake, Limits(window: 128_000));
        var request = ToolConversation(groups: 4);

        Assert.AreEqual("Recovered", await RunAsync(client, request, streaming));

        Assert.AreEqual(2, fake.CallCount);
        Assert.AreSame(request[0], fake.Requests[1][0], "The user's request is never rewritten.");
        var retried = Results(fake.Requests[1]);
        StringAssert.Contains(retried[0], "middle omitted", "The oldest tool output is excerpted first.");
        Assert.AreEqual(8000, retried[^1].Length, "Trimming stops once the request is under target.");
        Assert.IsTrue(Results(request).All(result => result.Length == 8000), "The caller's messages are left as they were.");
        Assert.IsTrue(ChatRunSafetyScope.EstimateTokens(fake.Requests[1]) <= ChatRunSafetyScope.EstimateTokens(request) * 8 / 10);
    }

    [TestMethod]
    public async Task Overflow_TrimsTheNewestToolOutputWhenItIsAllThereIs()
    {
        // Layer 1 always spares the latest two groups; a rejected request has no such luxury.
        var fake = FakeChatClient.WithTextResponse("Recovered").FailAt(0, await ScriptedProvider.FailureAsync(400, OpenAiResponses));
        var client = Recovery(fake, Limits(window: 128_000));

        Assert.AreEqual("Recovered", await RunAsync(client, ToolConversation(groups: 1), streaming: false));

        StringAssert.Contains(Results(fake.Requests[1]).Single(), "middle omitted");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Overflow_WithNothingTrimmable_StopsWithoutASecondCall(bool streaming)
    {
        var fake = FakeChatClient.WithTextResponse("unreachable").FailAt(0, await ScriptedProvider.FailureAsync(400, OpenAiChat));
        var client = Recovery(fake, Limits(window: 200_000));

        var stopped = await Assert.ThrowsExactlyAsync<FabrCoreRunStoppedException>(() =>
            RunAsync(client, [new(ChatRole.User, new string('x', 40_000))], streaming));

        Assert.AreEqual(RunStopReason.PromptTooLarge, stopped.Reason);
        StringAssert.Contains(stopped.Message, "'default'");
        StringAssert.Contains(stopped.Message, "128000 tokens");
        Assert.AreEqual(1, fake.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SecondOverflow_RaisesRunStoppedNotTheProviderException(bool streaming)
    {
        var rejection = await ScriptedProvider.FailureAsync(400, OpenAiResponses);
        var fake = FakeChatClient.WithTextResponse("unreachable").FailAt(0, rejection).FailAt(1, rejection);
        var client = Recovery(fake, Limits(window: 128_000));

        var stopped = await Assert.ThrowsExactlyAsync<FabrCoreRunStoppedException>(() =>
            RunAsync(client, ToolConversation(groups: 4), streaming));

        Assert.AreEqual(RunStopReason.PromptTooLarge, stopped.Reason);
        Assert.AreEqual(2, fake.CallCount, "One retry, never a loop.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NonOverflowProviderErrors_PassThroughUntouched(bool streaming)
    {
        var rateLimited = await ScriptedProvider.FailureAsync(429, """{"error":{"message":"Rate limit reached","code":"rate_limit_exceeded"}}""");
        var fake = FakeChatClient.WithTextResponse("unreachable").FailAt(0, rateLimited);
        var client = Recovery(fake, Limits(window: 128_000));

        var thrown = await Assert.ThrowsAsync<Exception>(() => RunAsync(client, ToolConversation(groups: 4), streaming));

        Assert.AreSame(rateLimited, thrown);
        Assert.AreEqual(1, fake.CallCount);
    }

    [TestMethod]
    public async Task StreamingFailureAfterFirstContent_IsNotRetried()
    {
        var rejection = await ScriptedProvider.FailureAsync(400, OpenAiResponses);
        var inner = new StreamingScript(_ => Partial());
        var client = Recovery(inner, Limits(window: 128_000));

        var seen = new List<string>();
        var thrown = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(ToolConversation(groups: 4))) seen.Add(update.Text);
        });

        Assert.AreSame(rejection, thrown);
        CollectionAssert.AreEqual(new[] { "", "partial" }, seen, "Held updates are released once content arrives.");
        Assert.AreEqual(1, inner.Calls, "Content the caller has already seen cannot be taken back.");

        async IAsyncEnumerable<ChatResponseUpdate> Partial()
        {
            yield return new ChatResponseUpdate();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw rejection;
        }
    }

    [TestMethod]
    public async Task StreamingErrorEvent_IsRecoveredBeforeAnyContentIsYielded()
    {
        var inner = new StreamingScript(call => call == 0 ? Rejected() : Answer());
        var client = Recovery(inner, Limits(window: 128_000));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(ToolConversation(groups: 4))) updates.Add(update);

        Assert.AreEqual(2, inner.Calls);
        Assert.AreEqual("Recovered", string.Concat(updates.Select(update => update.Text)));
        Assert.IsFalse(updates.SelectMany(update => update.Contents).OfType<ErrorContent>().Any(), "The rejected attempt never reaches the caller.");
        StringAssert.Contains(Results(inner.Requests[1])[0], "middle omitted");

        static async IAsyncEnumerable<ChatResponseUpdate> Rejected()
        {
            await Task.Yield();
            yield return new ChatResponseUpdate();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new ErrorContent("Your input exceeds the context window of this model.") { ErrorCode = ContextOverflow.ErrorCode, Details = "input" }]);
        }

        static async IAsyncEnumerable<ChatResponseUpdate> Answer()
        {
            await Task.Yield();
            yield return new ChatResponseUpdate();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Recovered");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedAttempt_IsNotChargedToTheTurnBudget(bool streaming)
    {
        var answer = FakeChatClient.Text("Recovered");
        answer.Usage = new() { InputTokenCount = 100, OutputTokenCount = 5 };
        var fake = FakeChatClient.Scripted(answer).FailAt(0, await ScriptedProvider.FailureAsync(400, OpenAiResponses));
        var client = Recovery(fake, Limits(window: 128_000));

        using var safety = ChatRunSafetyScope.Begin("owner:agent", null, null, new(), null, null);
        await RunAsync(client, ToolConversation(groups: 4), streaming);

        Assert.AreEqual(1, safety.LlmCalls);
        Assert.AreEqual(100, safety.TurnCumulativeInputTokens);
    }

    [TestMethod]
    public async Task PhysicalGuard_FollowsAStatedLimit()
    {
        var limits = Limits(window: 64_000);
        var fake = FakeChatClient.WithTextResponse("ok");
        var tracked = new TokenTrackingChatClient(fake) { ContextWindowTokens = 64_000, Limits = limits };
        ChatMessage[] prompt = [new(ChatRole.User, new string('x', 10_000 * 4))];

        await tracked.GetResponseAsync(prompt);
        limits.Record(new(8_000, 9_000, ""), promptEstimate: 9_000);

        await Assert.ThrowsExactlyAsync<FabrCoreRunStoppedException>(() => tracked.GetResponseAsync(prompt));
        Assert.AreEqual(1, fake.CallCount);
    }

    // ── End to end through the agent proxy ──

    [TestMethod]
    public async Task LearnedLimit_TightensLayerOneForLaterCallsInTheSameActivation()
    {
        // The model is configured with a 64,000 window but the provider enforces 8,000. Ten 2,000-token
        // tool results never trip layer 1 at 64,000, so the fifth call is rejected.
        var registry = new ModelContextLimitRegistry();
        var client = FakeChatClient.Scripted(
        [
            .. Enumerable.Range(0, 10).Select(i => FakeChatClient.ToolCall($"call{i}", "fetch", "{}")),
            FakeChatClient.Text("Finished")
        ]).FailAt(4, await ScriptedProvider.FailureAsync(400, SmallWindow));
        var (agent, _) = await LimitsTestAgent.CreateAsync(
            window: 64_000,
            client: client,
            tools: [AIFunctionFactory.Create(() => new string('x', 8000), "fetch")],
            registry: registry);
        Assert.AreEqual(64_000, agent.Ladder!.Context.MaxContextWindowTokens);

        var response = await agent.AskAsync("Fetch records");

        Assert.AreEqual("Finished", response.Message, response.Message);
        Assert.AreEqual(12, client.CallCount, "Eleven model turns plus the one rejected attempt.");
        Assert.IsTrue(
            client.Requests.Skip(5).All(request => ChatRunSafetyScope.EstimateTokens(request) < 8_000),
            "After the rejection every call is trimmed against the stated limit, not the configured window.");

        // The ladder follows: history, the fuse and the stop now sit under the stated limit.
        var ladder = agent.Ladder!;
        Assert.IsTrue(ladder.Context.WindowIsLearned);
        Assert.AreEqual(8_000, ladder.Context.MaxContextWindowTokens);
        Assert.AreEqual(8_000, ladder.RunSafety.MaxPromptInputTokens);
        StringAssert.Contains(ladder.Describe(), "(window 8000 stated by provider)");

        // A new activation on the same host starts from what was learned.
        var (next, _) = await LimitsTestAgent.CreateAsync(window: 64_000, registry: registry);
        Assert.AreEqual(8_000, next.Ladder!.Context.MaxContextWindowTokens);

        // Without a shared registry nothing leaks between activations.
        var (isolated, _) = await LimitsTestAgent.CreateAsync(window: 64_000);
        Assert.AreEqual(64_000, isolated.Ladder!.Context.MaxContextWindowTokens);
    }

    // ── Helpers ──

    private static ModelContextLimits Limits(int? window, int? output = null, ModelContextLimitRegistry? registry = null)
    {
        var limits = new ModelContextLimits("default", registry ?? new ModelContextLimitRegistry());
        Configure(limits, window, output);
        return limits;
    }

    private static void Configure(ModelContextLimits limits, int? window, int? output = null)
    {
        var derived = window is > 0 && output is null;
        limits.Configure(
            new ModelConfiguration
            {
                Name = "default", Provider = "Test", Uri = "http://localhost", Model = "test", ApiKeyAlias = "test",
                ContextWindowTokens = window, MaxOutputTokens = output
            },
            new ContextCompactionConfig
            {
                MaxContextWindowTokens = window ?? 0,
                MaxOutputTokens = derived ? ContextCompaction.DeriveOutputReserve(window!.Value) : output ?? 0,
                OutputReserveIsDerived = derived
            });
    }

    private static IChatClient Recovery(IChatClient inner, ModelContextLimits limits) =>
        new ContextOverflowRecoveryChatClient(
            new TokenTrackingChatClient(inner) { ContextWindowTokens = limits.Configured.MaxContextWindowTokens, Limits = limits },
            limits);

    private static async Task<string> RunAsync(IChatClient client, IReadOnlyList<ChatMessage> request, bool streaming)
    {
        if (!streaming)
            return (await client.GetResponseAsync(request)).Text;

        var text = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync(request)) text += update.Text;
        return text;
    }

    /// <summary>A user request followed by complete tool-call groups, each returning 8,000 characters.</summary>
    private static List<ChatMessage> ToolConversation(int groups)
    {
        List<ChatMessage> messages = [new(ChatRole.User, "Fetch the records.")];
        for (var i = 0; i < groups; i++)
        {
            messages.Add(new(ChatRole.Assistant, [new FunctionCallContent($"call{i}", "fetch")]));
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent($"call{i}", new string('x', 8000))]));
        }

        return messages;
    }

    private static List<string> Results(IEnumerable<ChatMessage> messages) =>
        [.. messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => (string)result.Result!)];

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>A streaming-only client whose response to each call is supplied by the test.</summary>
    private sealed class StreamingScript(Func<int, IAsyncEnumerable<ChatResponseUpdate>> script) : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public int Calls => Requests.Count;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            await foreach (var update in script(Requests.Count - 1).WithCancellation(cancellationToken))
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
