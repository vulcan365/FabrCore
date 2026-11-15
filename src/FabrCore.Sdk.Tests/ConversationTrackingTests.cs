using System.Reflection;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class ConversationTrackingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Tracking_IsDisabledByDefault()
    {
        var client = FakeChatClient.WithTextResponse("unused");
        var service = new FakeChatClientService(client);
        var host = new FakeAgentHost("owner:assistant");
        var proxy = CreateProxy(host, service);

        Assert.IsNotNull(await SendAsync(proxy, "release-ops", "Plan the release."));
        Assert.IsEmpty(await proxy.ConversationsAsync());
        Assert.IsFalse(host.CustomState.ContainsKey("_ConversationCatalog"));
        Assert.AreEqual(0, client.CallCount);
        Assert.AreEqual(0, service.RequestedClients.Count);
    }

    [TestMethod]
    public async Task Tracking_IgnoresBlankChannelsButPreservesExactNonblankIdentity()
    {
        var client = FakeChatClient.WithTextResponse("Release planning.");
        var service = new FakeChatClientService(client);
        var host = new FakeAgentHost("owner:assistant");
        var proxy = CreateProxy(host, service, EnabledArgs());

        foreach (var channel in new string?[] { null, "", "   ", "\t\r\n" })
            await SendAsync(proxy, channel, "No channel here.");

        Assert.IsEmpty(await proxy.ConversationsAsync());
        Assert.IsFalse(host.CustomState.ContainsKey("_ConversationCatalog"));
        Assert.AreEqual(0, client.CallCount);

        await SendAsync(proxy, " release-ops ", "First conversation.");
        await SendAsync(proxy, "release-ops", "Second conversation.");
        await SendAsync(proxy, " release-ops ", "Existing conversation.");

        var conversations = await proxy.ConversationsAsync();
        Assert.AreEqual(2, conversations.Count);
        CollectionAssert.AreEquivalent(new[] { " release-ops ", "release-ops" }, conversations.Select(c => c.Channel).ToArray());
        Assert.AreEqual(2, client.CallCount);
        var entries = host.CustomState["_ConversationCatalog"].GetProperty("Conversations");
        Assert.AreEqual(" release-ops ", entries.GetProperty(" release-ops ").GetProperty("Channel").GetString());
        Assert.AreEqual("release-ops", entries.GetProperty("release-ops").GetProperty("Channel").GetString());
    }

    [TestMethod]
    public async Task Tracking_PersistsProvisionalEntryBeforeWorkerResponds()
    {
        using var client = new DelayedChatClient();
        var host = new FakeAgentHost("owner:assistant");
        var proxy = CreateProxy(host, new FakeChatClientService(client), EnabledArgs());
        var before = DateTime.UtcNow;
        var message = SendAsync(proxy, "release-ops", "Plan the release. Include rollback.");

        try
        {
            await client.Started(0).WaitAsync(TestTimeout);
            var stored = host.CustomState["_ConversationCatalog"].GetProperty("Conversations").GetProperty("release-ops");
            Assert.AreEqual("Plan the release.", stored.GetProperty("Description").GetString());
            Assert.IsTrue(stored.GetProperty("IsProvisional").GetBoolean());
            var provisional = (await proxy.ConversationsAsync()).Single();
            Assert.AreEqual("release-ops", provisional.Channel);
            Assert.AreEqual("Plan the release.", provisional.Description);
            Assert.IsTrue(provisional.IsProvisional);
            Assert.IsTrue(provisional.CreatedAtUtc >= before && provisional.CreatedAtUtc <= DateTime.UtcNow);
            Assert.IsTrue(provisional.UpdatedAtUtc >= provisional.CreatedAtUtc);

            client.Complete(0, "Deployment and rollback planning.");
            Assert.IsNotNull(await message.WaitAsync(TestTimeout));
            var completed = (await proxy.ConversationsAsync()).Single();
            Assert.AreEqual("Deployment and rollback planning.", completed.Description);
            Assert.IsFalse(completed.IsProvisional);
            Assert.AreEqual(provisional.CreatedAtUtc, completed.CreatedAtUtc);
            Assert.IsTrue(completed.UpdatedAtUtc >= provisional.UpdatedAtUtc);
        }
        finally
        {
            client.CompleteAll();
        }
    }

    [TestMethod]
    public async Task Tracking_ConcurrentMessagesForSameChannelShareFirstWorker()
    {
        using var client = new DelayedChatClient();
        var host = new FakeAgentHost("owner:assistant");
        var proxy = CreateProxy(host, new FakeChatClientService(client), EnabledArgs());
        var first = SendAsync(proxy, "release-ops", "First request. More detail.");
        try
        {
            await client.Started(0).WaitAsync(TestTimeout);
            var provisional = (await proxy.ConversationsAsync()).Single();
            Assert.AreEqual("First request.", provisional.Description);
            Assert.IsTrue(provisional.IsProvisional);

            var second = SendAsync(proxy, "release-ops", "Second request.");
            await second.WaitAsync(TestTimeout);
            Assert.AreEqual("First request. More detail.", proxy.CurrentMessage?.Message);
            Assert.IsFalse(first.IsCompleted);
            Assert.AreEqual(1, client.CallCount, "A second message must not start another worker.");
            var during = host.CustomState["_ConversationCatalog"].GetProperty("Conversations").GetProperty("release-ops");
            Assert.AreEqual(provisional.Description, during.GetProperty("Description").GetString());
            Assert.IsTrue(during.GetProperty("IsProvisional").GetBoolean());
            Assert.AreEqual(provisional.CreatedAtUtc, during.GetProperty("CreatedAtUtc").GetDateTime());

            client.Complete(0, "First worker summary.");
            await Task.WhenAll(first, second).WaitAsync(TestTimeout);
            var final = (await proxy.ConversationsAsync()).Single();
            Assert.AreEqual("First worker summary.", final.Description);
            Assert.IsFalse(final.IsProvisional);
            Assert.AreEqual(provisional.CreatedAtUtc, final.CreatedAtUtc);
            Assert.AreEqual(1, client.CallCount);
            var stored = host.CustomState["_ConversationCatalog"].GetProperty("Conversations").GetProperty("release-ops");
            Assert.AreEqual(final.Description, stored.GetProperty("Description").GetString());
            Assert.IsFalse(stored.GetProperty("IsProvisional").GetBoolean());
        }
        finally
        {
            client.CompleteAll();
        }
    }

    [TestMethod]
    public async Task Tracking_UpdatesDoNotFlushUnrelatedPendingState()
    {
        using var client = new DelayedChatClient();
        var host = new FakeAgentHost("owner:assistant");
        var proxy = CreateProxy(host, new FakeChatClientService(client), EnabledArgs());
        proxy.SetTestState("unrelated", "pending value");
        var message = SendAsync(proxy, "release-ops", "First request.");
        try
        {
            await client.Started(0).WaitAsync(TestTimeout);
            Assert.IsTrue(proxy.HasPendingTestState);
            Assert.IsFalse(host.CustomState.ContainsKey("unrelated"));
            Assert.IsTrue(host.CustomState["_ConversationCatalog"].GetProperty("Conversations")
                .GetProperty("release-ops").GetProperty("IsProvisional").GetBoolean());

            client.Complete(0, "Final summary.");
            await message.WaitAsync(TestTimeout);
            Assert.IsTrue(proxy.HasPendingTestState);
            Assert.IsFalse(host.CustomState.ContainsKey("unrelated"));
            Assert.IsFalse(host.CustomState["_ConversationCatalog"].GetProperty("Conversations")
                .GetProperty("release-ops").GetProperty("IsProvisional").GetBoolean());

            await proxy.FlushTestStateAsync();
            Assert.IsFalse(proxy.HasPendingTestState);
            Assert.AreEqual("pending value", host.CustomState["unrelated"].GetString());
            Assert.AreEqual("Final summary.", host.CustomState["_ConversationCatalog"].GetProperty("Conversations")
                .GetProperty("release-ops").GetProperty("Description").GetString());
        }
        finally
        {
            client.CompleteAll();
        }
    }

    [TestMethod]
    public async Task Tracking_DifferentChannelWritesSerializeAcrossAsynchronousMerge()
    {
        var host = new ControlledMergeHost("owner:assistant") { BlockFirstMerge = true };
        var service = new FakeChatClientService(FakeChatClient.WithTextResponse("Final summary."));
        var proxy = CreateProxy(host, service, EnabledArgs());
        var first = SendAsync(proxy, "alpha", "Alpha request.");
        try
        {
            await host.FirstMergeStarted.WaitAsync(TestTimeout);
            // SendAsync enters tracking synchronously; the host's state reads also complete synchronously.
            var second = SendAsync(proxy, "beta", "Beta request.");
            Assert.AreEqual("beta", proxy.CurrentMessage?.Channel, "The second message must enter the proxy before the first merge is released.");
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual(1, host.StateReadCount, "The second tracking operation must wait before reading the catalog.");
            Assert.AreEqual(1, host.MergeCount, "The second channel must wait for the first catalog write.");
            host.ReleaseFirstMerge();
            await Task.WhenAll(first, second).WaitAsync(TestTimeout);
            Assert.IsFalse(host.OverlappingMerges);

            var restored = CreateProxy(host, service, EnabledArgs());
            var entries = (await restored.ConversationsAsync()).ToDictionary(c => c.Channel);
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual("Final summary.", entries["alpha"].Description);
            Assert.AreEqual("Final summary.", entries["beta"].Description);
            Assert.IsFalse(entries["alpha"].IsProvisional);
            Assert.IsFalse(entries["beta"].IsProvisional);
            var durable = host.DurableState["_ConversationCatalog"].GetProperty("Conversations");
            Assert.AreEqual("Final summary.", durable.GetProperty("alpha").GetProperty("Description").GetString());
            Assert.AreEqual("Final summary.", durable.GetProperty("beta").GetProperty("Description").GetString());
            Assert.IsFalse(durable.GetProperty("alpha").GetProperty("IsProvisional").GetBoolean());
            Assert.IsFalse(durable.GetProperty("beta").GetProperty("IsProvisional").GetBoolean());
        }
        finally
        {
            host.ReleaseFirstMerge();
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Tracking_RetriesPersistenceAfterHostMutatesLiveStateThenFails(int failingMerge)
    {
        var host = new ControlledMergeHost("owner:assistant") { FailMerge = failingMerge };
        var client = FakeChatClient.WithTextResponse("Final summary.");
        var service = new FakeChatClientService(client);
        var proxy = CreateProxy(host, service, EnabledArgs());

        Assert.IsNotNull(await SendAsync(proxy, "release-ops", "First request.").WaitAsync(TestTimeout));
        Assert.AreEqual(failingMerge, host.MergeCount);
        var live = host.LiveState["_ConversationCatalog"].GetProperty("Conversations").GetProperty("release-ops");
        Assert.AreEqual(failingMerge == 1, live.GetProperty("IsProvisional").GetBoolean());
        if (failingMerge == 1)
            Assert.IsFalse(host.DurableState.ContainsKey("_ConversationCatalog"));
        else
            Assert.IsTrue(host.DurableState["_ConversationCatalog"].GetProperty("Conversations")
                .GetProperty("release-ops").GetProperty("IsProvisional").GetBoolean());

        Assert.IsNotNull(await SendAsync(proxy, "release-ops", "Follow-up request.").WaitAsync(TestTimeout));
        Assert.IsTrue(host.MergeCount > failingMerge, "The next message must retry the failed write.");
        Assert.AreEqual(1, client.CallCount);
        var durable = host.DurableState["_ConversationCatalog"].GetProperty("Conversations").GetProperty("release-ops");
        Assert.AreEqual("Final summary.", durable.GetProperty("Description").GetString());
        Assert.IsFalse(durable.GetProperty("IsProvisional").GetBoolean());
    }

    [TestMethod]
    public async Task Tracking_RetriesFailedWriteEvenWhenHostDidNotMutateState()
    {
        var host = new ControlledMergeHost("owner:assistant") { FailMerge = 1, FailBeforeMutation = true };
        var service = new FakeChatClientService(FakeChatClient.WithTextResponse("Final summary."));
        var proxy = CreateProxy(host, service, EnabledArgs());

        await SendAsync(proxy, "alpha", "Alpha request.");
        Assert.IsFalse(host.LiveState.ContainsKey("_ConversationCatalog"));
        await SendAsync(proxy, "beta", "Beta request.");
        var durable = host.DurableState["_ConversationCatalog"].GetProperty("Conversations");
        Assert.AreEqual("Alpha request.", durable.GetProperty("alpha").GetProperty("Description").GetString());
        Assert.IsTrue(durable.GetProperty("alpha").GetProperty("IsProvisional").GetBoolean());
        Assert.AreEqual("Final summary.", durable.GetProperty("beta").GetProperty("Description").GetString());
    }

    [TestMethod]
    public async Task Tracking_InterleavedChannelsSurviveOutOfOrderWorkerCompletions()
    {
        using var client = new DelayedChatClient();
        var host = new FakeAgentHost("owner:assistant");
        var proxy = CreateProxy(host, new FakeChatClientService(client), EnabledArgs());
        var first = SendAsync(proxy, "alpha", "Alpha request.");
        Task<AgentMessage>? second = null;

        try
        {
            await client.Started(0).WaitAsync(TestTimeout);
            second = SendAsync(proxy, "beta", "Beta request.");
            await client.Started(1).WaitAsync(TestTimeout);
            var provisional = (await proxy.ConversationsAsync()).ToDictionary(c => c.Channel);
            Assert.AreEqual(2, provisional.Count);
            Assert.AreEqual("Alpha request.", provisional["alpha"].Description);
            Assert.AreEqual("Beta request.", provisional["beta"].Description);
            Assert.IsTrue(provisional.Values.All(c => c.IsProvisional));

            client.Complete(1, "Beta summary.");
            await second.WaitAsync(TestTimeout);
            client.Complete(0, "Alpha summary.");
            await first.WaitAsync(TestTimeout);

            var reactivated = CreateProxy(host, new FakeChatClientService(FakeChatClient.WithTextResponse("unused")), EnabledArgs());
            var final = (await reactivated.ConversationsAsync()).ToDictionary(c => c.Channel);
            Assert.AreEqual(2, final.Count);
            Assert.AreEqual("Alpha summary.", final["alpha"].Description);
            Assert.AreEqual("Beta summary.", final["beta"].Description);
            Assert.IsFalse(final["alpha"].IsProvisional);
            Assert.IsFalse(final["beta"].IsProvisional);
            Assert.AreEqual(provisional["alpha"].CreatedAtUtc, final["alpha"].CreatedAtUtc);
            Assert.AreEqual(provisional["beta"].CreatedAtUtc, final["beta"].CreatedAtUtc);
        }
        finally
        {
            client.CompleteAll();
        }
    }

    [TestMethod]
    public async Task Tracking_PersistsAcrossProxyActivationsWithoutRegenerating()
    {
        var client = FakeChatClient.WithTextResponse("Release planning and safeguards.");
        var service = new FakeChatClientService(client);
        var host = new FakeAgentHost("owner:assistant");
        var first = CreateProxy(host, service, EnabledArgs());
        await SendAsync(first, "release-ops", "Plan the release.");
        var original = (await first.ConversationsAsync()).Single();

        var second = CreateProxy(host, service, EnabledArgs());
        var restored = (await second.ConversationsAsync()).Single();
        Assert.AreEqual(original.Channel, restored.Channel);
        Assert.AreEqual(original.Description, restored.Description);
        Assert.AreEqual(original.IsProvisional, restored.IsProvisional);
        Assert.AreEqual(original.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.AreEqual(original.UpdatedAtUtc, restored.UpdatedAtUtc);
        await SendAsync(second, "release-ops", "Continue the release.");
        Assert.AreEqual(1, client.CallCount);
        Assert.AreEqual(original.Description, (await second.ConversationsAsync()).Single().Description);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Tracking_WorkerFailureKeepsProvisionalAndRetriesOnLaterMessage(bool canceled)
    {
        var client = FakeChatClient.WithTextResponse("Recovered release summary.")
            .FailAt(0, canceled ? new OperationCanceledException("worker canceled") : new InvalidOperationException("worker unavailable"));
        var service = new FakeChatClientService(client);
        var host = new FakeAgentHost("owner:assistant");
        var first = CreateProxy(host, service, EnabledArgs());

        Assert.IsNotNull(await SendAsync(first, "release-ops", "Summarize the rollout. Include safeguards."));
        var provisional = (await first.ConversationsAsync()).Single();
        Assert.AreEqual("Summarize the rollout.", provisional.Description);
        Assert.IsTrue(provisional.IsProvisional);
        Assert.IsTrue(host.CustomState["_ConversationCatalog"].GetProperty("Conversations")
            .GetProperty("release-ops").GetProperty("IsProvisional").GetBoolean());

        var reactivated = CreateProxy(host, service, EnabledArgs());
        Assert.IsNotNull(await SendAsync(reactivated, "release-ops", "Continue planning."));
        var recovered = (await reactivated.ConversationsAsync()).Single();
        Assert.AreEqual(2, client.CallCount);
        Assert.AreEqual("Recovered release summary.", recovered.Description);
        Assert.IsFalse(recovered.IsProvisional);
        Assert.AreEqual(provisional.CreatedAtUtc, recovered.CreatedAtUtc);
        Assert.IsTrue(recovered.UpdatedAtUtc >= provisional.UpdatedAtUtc);
    }

    [TestMethod]
    [DataRow("worker-small", "main-model", "worker-small")]
    [DataRow(null, "main-model", "main-model")]
    [DataRow("  ", "main-model", "main-model")]
    [DataRow(null, "", "default")]
    [DataRow(null, "  ", "default")]
    public async Task WorkerClient_UsesOverrideThenConfiguredModelThenDefault(string? workerModel, string models, string expected)
    {
        var service = new RecordingChatClientService();
        var args = EnabledArgs();
        if (workerModel is not null)
            args["_ConversationWorkerModel"] = workerModel;
        var proxy = CreateProxy(new FakeAgentHost("owner:assistant"), service, args, models);

        Assert.IsNotNull(await proxy.WorkerClientAsync());
        Assert.IsNotNull(await proxy.WorkerClientAsync(37));
        await SendAsync(proxy, "channel", "A request.");
        CollectionAssert.AreEqual(new[] { expected, expected, expected }, service.Requests.Select(r => r.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 100, 37, 100 }, service.Requests.Select(r => r.Timeout).ToArray());
    }

    [TestMethod]
    public async Task Tracking_LimitsBothProvisionalAndWorkerDescriptionsTo74Words()
    {
        var longText = string.Join(' ', Enumerable.Range(1, 100).Select(i => $"word{i}"));
        using var client = new DelayedChatClient();
        var proxy = CreateProxy(new FakeAgentHost("owner:assistant"), new FakeChatClientService(client), EnabledArgs());
        var message = SendAsync(proxy, "long", longText);
        try
        {
            await client.Started(0).WaitAsync(TestTimeout);
            var provisional = (await proxy.ConversationsAsync()).Single();
            Assert.IsTrue(provisional.IsProvisional);
            Assert.IsTrue(WordCount(provisional.Description) is > 0 and <= 74);
            client.Complete(0, longText);
            await message.WaitAsync(TestTimeout);
            var completed = (await proxy.ConversationsAsync()).Single();
            Assert.IsFalse(completed.IsProvisional);
            Assert.IsTrue(WordCount(completed.Description) is > 0 and <= 74);
        }
        finally
        {
            client.CompleteAll();
        }
    }

    [TestMethod]
    public void Tracking_ApisAreProtectedAndMetadataIsPublic()
    {
        var conversations = typeof(FabrCoreAgentProxy).GetMethod("GetConversationsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(conversations);
        Assert.IsTrue(conversations.IsFamily);
        Assert.AreEqual(typeof(Task<IReadOnlyList<ConversationMetadata>>), conversations.ReturnType);
        Assert.AreEqual(0, conversations.GetParameters().Length);

        var worker = typeof(FabrCoreAgentProxy).GetMethod("GetWorkerChatClientAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(worker);
        Assert.IsTrue(worker.IsFamily);
        Assert.AreEqual(typeof(Task<IChatClient>), worker.ReturnType);
        var parameter = worker.GetParameters().Single();
        Assert.AreEqual(typeof(int), parameter.ParameterType);
        Assert.AreEqual(100, parameter.DefaultValue);

        Assert.IsTrue(typeof(ConversationMetadata).IsPublic);
        foreach (var name in new[] { "Channel", "Description", "IsProvisional", "CreatedAtUtc", "UpdatedAtUtc" })
            Assert.IsNotNull(typeof(ConversationMetadata).GetProperty(name, BindingFlags.Instance | BindingFlags.Public));
    }

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static Dictionary<string, string> EnabledArgs() => new() { ["_ConversationTrackingEnabled"] = "true" };

    private static Task<AgentMessage> SendAsync(ConversationTrackingTestProxy proxy, string? channel, string message)
        => ((IFabrCoreAgentProxy)proxy).InternalOnMessage(new AgentMessage
        {
            FromHandle = "owner",
            ToHandle = "owner:assistant",
            Channel = channel,
            Message = message
        });

    private static ConversationTrackingTestProxy CreateProxy(
        IFabrCoreAgentHost host,
        IFabrCoreChatClientService chatClientService,
        Dictionary<string, string>? args = null,
        string models = "default")
    {
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(chatClientService)
            .BuildServiceProvider();

        return new ConversationTrackingTestProxy(
            new AgentConfiguration
            {
                Handle = host.GetHandle(),
                AgentType = "conversation-test-agent",
                Models = models,
                Args = args ?? new Dictionary<string, string>()
            },
            services,
            host);
    }

    private sealed class ConversationTrackingTestProxy(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost host) : FabrCoreAgentProxy(config, serviceProvider, host)
    {
        public AgentMessage? CurrentMessage => ActiveMessage;
        public Task<IReadOnlyList<ConversationMetadata>> ConversationsAsync() => GetConversationsAsync();
        public void SetTestState<T>(string key, T value) => SetState(key, value);
        public bool HasPendingTestState => HasPendingStateChanges;
        public Task FlushTestStateAsync() => FlushStateAsync();
        public Task<IChatClient> WorkerClientAsync(int networkTimeoutSeconds = 100) => GetWorkerChatClientAsync(networkTimeoutSeconds);
        public override Task OnInitialize() => Task.CompletedTask;
        public override Task<AgentMessage> OnMessage(AgentMessage message) => Task.FromResult(message.Response());
    }

    private sealed class ControlledMergeHost(string handle) : IFabrCoreAgentHost
    {
        private readonly FakeAgentHost inner = new(handle);
        private readonly TaskCompletionSource<bool> firstMergeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseFirstMerge = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int mergeCount;
        private int stateReadCount;
        private int activeMerges;
        private int overlappingMerges;

        public bool BlockFirstMerge { get; init; }
        public int FailMerge { get; init; }
        public bool FailBeforeMutation { get; init; }
        public int MergeCount => Volatile.Read(ref mergeCount);
        public int StateReadCount => Volatile.Read(ref stateReadCount);
        public bool OverlappingMerges => Volatile.Read(ref overlappingMerges) != 0;
        public Task FirstMergeStarted => firstMergeStarted.Task;
        public Dictionary<string, JsonElement> LiveState => inner.CustomState;
        public Dictionary<string, JsonElement> DurableState { get; } = [];
        public void ReleaseFirstMerge() => releaseFirstMerge.TrySetResult(true);

        public async Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
        {
            var call = Interlocked.Increment(ref mergeCount);
            if (Interlocked.Increment(ref activeMerges) != 1)
                Interlocked.Exchange(ref overlappingMerges, 1);
            try
            {
                if (call == 1)
                {
                    firstMergeStarted.TrySetResult(true);
                    if (BlockFirstMerge)
                        await releaseFirstMerge.Task;
                }

                if (call == FailMerge && FailBeforeMutation)
                    throw new IOException("Simulated persistence failure before live state mutation.");
                await inner.MergeCustomStateAsync(changes, deletes);
                if (call == FailMerge)
                    throw new IOException("Simulated persistence failure after live state mutation.");

                foreach (var key in deletes)
                    DurableState.Remove(key);
                foreach (var (key, value) in changes)
                    DurableState[key] = value;
            }
            finally
            {
                Interlocked.Decrement(ref activeMerges);
            }
        }

        public string GetHandle() => inner.GetHandle();
        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request) => inner.SendAndReceiveMessage(request);
        public Task SendMessage(AgentMessage request) => inner.SendMessage(request);
        public Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
            => inner.GetAgentHealth(handle, detailLevel);
        public Task SendEvent(EventMessage request) => inner.SendEvent(request);
        public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
            => inner.RegisterTimer(timerName, messageType, message, dueTime, period);
        public void UnregisterTimer(string timerName) => inner.UnregisterTimer(timerName);
        public Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
            => inner.RegisterReminder(reminderName, messageType, message, dueTime, period);
        public Task UnregisterReminder(string reminderName) => inner.UnregisterReminder(reminderName);
        public FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId) => inner.GetChatHistoryProvider(threadId);
        public void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider) => inner.TrackChatHistoryProvider(provider);
        public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId) => inner.GetThreadMessagesAsync(threadId);
        public Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages) => inner.AddThreadMessagesAsync(threadId, messages);
        public Task ClearThreadAsync(string threadId) => inner.ClearThreadAsync(threadId);
        public Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => inner.ReplaceThreadMessagesAsync(threadId, messages);
        public Task<Dictionary<string, JsonElement>> GetCustomStateAsync()
        {
            Interlocked.Increment(ref stateReadCount);
            return inner.GetCustomStateAsync();
        }
        public void SetStatusMessage(string? message) => inner.SetStatusMessage(message);
    }

    private sealed class RecordingChatClientService : IFabrCoreChatClientService
    {
        private readonly IChatClient client = FakeChatClient.WithTextResponse("Worker summary.");
        public List<(string Name, int Timeout)> Requests { get; } = [];

        public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
        {
            Requests.Add((name, networkTimeoutSeconds));
            return Task.FromResult(client);
        }

#pragma warning disable MEAI001
        public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100) => throw new NotSupportedException();
#pragma warning restore MEAI001
        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name) => throw new NotSupportedException();
        public Task<ModelConfiguration> GetModelConfigurationAsync(string name) => Task.FromResult(new ModelConfiguration
        {
            Name = name, Provider = "Test", Uri = "http://localhost", Model = name, ApiKeyAlias = "test"
        });
    }

    private sealed class DelayedChatClient : IChatClient
    {
        private readonly TaskCompletionSource<bool>[] started = Enumerable.Range(0, 2)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource<ChatResponse>[] responses = Enumerable.Range(0, 2)
            .Select(_ => new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private int calls;

        public int CallCount => Volatile.Read(ref calls);
        public Task Started(int index) => started[index].Task;
        public void Complete(int index, string text) => responses[index].TrySetResult(FakeChatClient.Text(text));
        public void CompleteAll()
        {
            foreach (var response in responses)
                response.TrySetResult(FakeChatClient.Text("Cleanup."));
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref calls) - 1;
            if (index >= responses.Length)
                throw new InvalidOperationException("Unexpected worker call.");
            started[index].TrySetResult(true);
            return responses[index].Task;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(chatMessages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => CompleteAll();
    }
}
