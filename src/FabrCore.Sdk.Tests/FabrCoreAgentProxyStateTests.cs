using FabrCore.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace FabrCore.Sdk.Tests;

[TestClass]
public sealed class FabrCoreAgentProxyStateTests
{
    [TestMethod]
    public async Task SendToUserText_TargetsOwningPrincipalAndPreservesDeliveryTarget()
    {
        var agent = CreateAgent(new Dictionary<string, JsonElement>());

        await agent.SendText(
            "Report ready",
            "report.ready",
            new PrincipalDeliveryTarget("sms", "phone-1"));

        var sent = agent.SentMessage;
        Assert.IsNotNull(sent);
        Assert.AreEqual("owner", sent.ToHandle);
        Assert.AreEqual(MessageKind.OneWay, sent.Kind);
        Assert.AreEqual("Report ready", sent.Message);
        Assert.AreEqual("report.ready", sent.MessageType);
        Assert.AreEqual("sms", sent.DeliveryTarget?.Channel);
        Assert.AreEqual("phone-1", sent.DeliveryTarget?.EndpointId);
    }

    [TestMethod]
    public async Task SendToUserStructured_PreservesStructuredPayload()
    {
        var agent = CreateAgent(new Dictionary<string, JsonElement>());
        var message = new AgentMessage
        {
            DataType = "application/example+json",
            Data = [1, 2, 3],
            Args = new Dictionary<string, string> { ["source"] = "test" }
        };

        await agent.SendStructured(message);

        Assert.AreSame(message, agent.SentMessage);
        Assert.AreEqual("owner", message.ToHandle);
        Assert.AreEqual(MessageKind.OneWay, message.Kind);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, message.Data);
        Assert.AreEqual("test", message.Args?["source"]);
    }

    [TestMethod]
    public async Task ALapsedConnectionsNoticeGoesToThePerson_ForATokenAndForARequest()
    {
        var notice = new FabrCore.Connections.ConnectionException("interaction-required", "Lapsed.") { Notice = "Connect it again." };
        var agent = CreateAgent(new Dictionary<string, JsonElement>(), new LapsedConnections(notice));

        var fromToken = await Assert.ThrowsExactlyAsync<FabrCore.Connections.ConnectionException>(() => agent.Token());
        Assert.AreSame(notice, fromToken);
        Assert.AreEqual("Connect it again.", agent.SentMessage?.Message);
        Assert.AreEqual("owner", agent.SentMessage?.ToHandle);

        ((TestAgentHost)agent.Host).Clear();
        using var client = await agent.Client();
        await Assert.ThrowsExactlyAsync<FabrCore.Connections.ConnectionException>(() => client.GetAsync("me/events"));
        Assert.AreEqual("Connect it again.", agent.SentMessage?.Message, "A client fetches its token as it sends, so the lapse shows there.");
    }

    [TestMethod]
    public async Task GetStateAsync_ReturnsDefault_WhenStateElementIsUndefined()
    {
        var agent = CreateAgent(new Dictionary<string, JsonElement>
        {
            ["surface-task-runner-state"] = default
        });

        var state = await agent.GetState<TestState>("surface-task-runner-state");

        Assert.IsNull(state);
    }

    [TestMethod]
    public async Task GetStateAsync_ReturnsDefault_WhenStateElementIsNull()
    {
        using var document = JsonDocument.Parse("null");
        var agent = CreateAgent(new Dictionary<string, JsonElement>
        {
            ["surface-task-runner-state"] = document.RootElement.Clone()
        });

        var state = await agent.GetState<TestState>("surface-task-runner-state");

        Assert.IsNull(state);
    }

    [TestMethod]
    public async Task TryGetStateAsync_ReturnsDiagnostics_WhenStateCannotDeserialize()
    {
        using var document = JsonDocument.Parse("""{"Count":"not-an-int"}""");
        var agent = CreateAgent(new Dictionary<string, JsonElement>
        {
            ["surface-task-runner-state"] = document.RootElement.Clone()
        });

        var result = await agent.TryGetState<TestState>("surface-task-runner-state");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("surface-task-runner-state", result.Key);
        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.IsInstanceOfType(result.Error, typeof(JsonException));
    }

    [TestMethod]
    public async Task GetStateAsync_ThrowsActionableException_WhenStateCannotDeserialize()
    {
        using var document = JsonDocument.Parse("""{"Count":"not-an-int"}""");
        var agent = CreateAgent(new Dictionary<string, JsonElement>
        {
            ["surface-task-runner-state"] = document.RootElement.Clone()
        });

        InvalidOperationException? ex = null;
        try
        {
            await agent.GetState<TestState>("surface-task-runner-state");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Assert.IsNotNull(ex);
        StringAssert.Contains(ex.Message, "surface-task-runner-state");
        StringAssert.Contains(ex.Message, "owner:swarm-test2");
        StringAssert.Contains(ex.Message, "surface-task-runner");
        StringAssert.Contains(ex.Message, typeof(TestState).FullName);
        StringAssert.Contains(ex.Message, "Stored value kind: Object");
        Assert.IsInstanceOfType(ex.InnerException, typeof(JsonException));
    }

    [TestMethod]
    public async Task AConnectionCarriesTheAgentsTypeAndTheHostPicksAnUndeclaredBinding()
    {
        var connections = new RecordingConnections();
        var agent = CreateAgent(new Dictionary<string, JsonElement>(), connections,
            services => services.AddSingleton<FabrCore.Connections.IConnectionBindingResolver>(new TypeResolver()));

        await agent.Token();
        using var _ = await agent.Client();

        Assert.HasCount(2, connections.Calls);
        foreach (var (caller, binding) in connections.Calls)
        {
            Assert.AreEqual(new FabrCore.Connections.ConnectionCaller("owner", "owner:swarm-test2", "surface-task-runner"), caller);
            Assert.AreEqual(new FabrCore.Connections.ConnectionBinding("host", "agentid-surface-task-runner"), binding);
        }
    }

    [TestMethod]
    public async Task WithoutAResolverAnUndeclaredConnectionIsTheAgentsOwnPrincipals()
    {
        var connections = new RecordingConnections();
        var agent = CreateAgent(new Dictionary<string, JsonElement>(), connections);

        await agent.Token();

        Assert.AreEqual(new FabrCore.Connections.ConnectionBinding("owner", "background"), connections.Calls.Single().Binding);
    }

    [TestMethod]
    public async Task ConcurrentFlushes_PreserveInFlightUpdatesDeletesAndSnapshots()
    {
        var stored = new Dictionary<string, JsonElement> { ["restore"] = JsonSerializer.SerializeToElement("original") };
        var agent = CreateAgent(stored);
        Assert.AreEqual("original", await agent.GetState<string>("restore"));
        var host = (TestAgentHost)agent.Host;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, JsonElement>? firstChanges = null;
        IEnumerable<string>? firstDeletes = null;
        var mergeCount = 0;
        host.BeforeMerge = async (changes, deletes) =>
        {
            if (Interlocked.Increment(ref mergeCount) == 1)
            {
                firstChanges = changes;
                firstDeletes = deletes;
                entered.SetResult();
                await release.Task;
            }
        };

        var mutable = new Dictionary<string, int> { ["value"] = 1 };
        agent.Set("update", mutable);
        agent.Set("remove", "old");
        agent.Remove("restore");
        var first = agent.Flush();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            mutable["value"] = 99;
            agent.Set("update", new Dictionary<string, int> { ["value"] = 2 });
            agent.Remove("remove");
            agent.Set("restore", "new");
            agent.Set("added", "later");
            Assert.AreEqual(2, (await agent.GetState<Dictionary<string, int>>("update"))!["value"]);
            Assert.IsNull(await agent.GetState<string>("remove"));
            Assert.AreEqual("new", await agent.GetState<string>("restore"));

            var second = agent.Flush();
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual(1, Volatile.Read(ref mergeCount));
            Assert.IsNotNull(firstChanges);
            Assert.IsNotNull(firstDeletes);
            Assert.AreEqual(1, firstChanges["update"].GetProperty("value").GetInt32());
            Assert.AreEqual("old", firstChanges["remove"].GetString());
            Assert.IsFalse(firstChanges.ContainsKey("added"));
            CollectionAssert.AreEquivalent(new[] { "restore" }, firstDeletes.ToArray());

            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, mergeCount);
            Assert.AreEqual(2, stored["update"].GetProperty("value").GetInt32());
            Assert.IsFalse(stored.ContainsKey("remove"));
            Assert.AreEqual("new", stored["restore"].GetString());
            Assert.AreEqual("later", stored["added"].GetString());
            Assert.IsFalse(agent.Pending);
            Assert.AreEqual(2, (await agent.GetState<Dictionary<string, int>>("update"))!["value"]);
            Assert.IsNull(await agent.GetState<string>("remove"));
            Assert.AreEqual("new", await agent.GetState<string>("restore"));
            Assert.AreEqual(1, firstChanges["update"].GetProperty("value").GetInt32());
            Assert.AreEqual("old", firstChanges["remove"].GetString());
            CollectionAssert.AreEquivalent(new[] { "restore" }, firstDeletes.ToArray());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task FailedMerge_RetryPreservesNewerChangesAndOriginalSnapshot()
    {
        var stored = new Dictionary<string, JsonElement>
        {
            ["restore"] = JsonSerializer.SerializeToElement("original"),
            ["delete-only"] = JsonSerializer.SerializeToElement("original")
        };
        var agent = CreateAgent(stored);
        Assert.AreEqual("original", await agent.GetState<string>("restore"));
        var host = (TestAgentHost)agent.Host;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, JsonElement>? failedChanges = null;
        IEnumerable<string>? failedDeletes = null;
        var failure = new InvalidOperationException("merge failed");
        var mergeCount = 0;
        host.BeforeMerge = async (changes, deletes) =>
        {
            if (Interlocked.Increment(ref mergeCount) == 1)
            {
                failedChanges = changes;
                failedDeletes = deletes;
                entered.SetResult();
                await release.Task;
                throw failure;
            }
        };

        agent.Set("unchanged", "keep");
        agent.Set("update", "old");
        agent.Set("remove", "old");
        agent.Remove("restore");
        var first = agent.Flush();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            agent.Set("update", "new");
            agent.Remove("remove");
            agent.Set("restore", "new");
            agent.Remove("delete-only");
            release.TrySetResult();

            var caught = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreSame(failure, caught);
            Assert.IsTrue(agent.Pending);
            Assert.AreEqual("original", stored["restore"].GetString());
            Assert.IsFalse(stored.ContainsKey("unchanged"));
            Assert.AreEqual("new", await agent.GetState<string>("update"));
            Assert.IsNull(await agent.GetState<string>("remove"));
            Assert.AreEqual("new", await agent.GetState<string>("restore"));
            Assert.IsNotNull(failedChanges);
            Assert.IsNotNull(failedDeletes);
            Assert.AreEqual("old", failedChanges["update"].GetString());
            Assert.AreEqual("old", failedChanges["remove"].GetString());
            CollectionAssert.AreEquivalent(new[] { "restore" }, failedDeletes.ToArray());

            await agent.Flush().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, mergeCount);
            Assert.AreEqual("keep", stored["unchanged"].GetString());
            Assert.AreEqual("new", stored["update"].GetString());
            Assert.AreEqual("new", stored["restore"].GetString());
            Assert.IsFalse(stored.ContainsKey("remove"));
            Assert.IsFalse(stored.ContainsKey("delete-only"));
            Assert.IsFalse(agent.Pending);
            Assert.AreEqual("keep", await agent.GetState<string>("unchanged"));
            Assert.AreEqual("new", await agent.GetState<string>("update"));
            Assert.IsNull(await agent.GetState<string>("remove"));
            Assert.AreEqual("new", await agent.GetState<string>("restore"));
            Assert.AreEqual("old", failedChanges["update"].GetString());
            Assert.AreEqual("old", failedChanges["remove"].GetString());
            CollectionAssert.AreEquivalent(new[] { "restore" }, failedDeletes.ToArray());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static TestAgentProxy CreateAgent(Dictionary<string, JsonElement> customState, FabrCore.Connections.IConnectionService? connections = null,
        Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IConfiguration>(new ConfigurationManager());
        services.AddSingleton<IFabrCoreChatClientService, TestChatClientService>();
        if (connections is not null)
            services.AddSingleton(connections);
        configure?.Invoke(services);
        var serviceProvider = services.BuildServiceProvider();

        return new TestAgentProxy(
            new AgentConfiguration
            {
                Handle = "owner:swarm-test2",
                AgentType = "surface-task-runner"
            },
            serviceProvider,
            new TestAgentHost("owner:swarm-test2", customState));
    }

    private sealed class TestState
    {
        public int Count { get; set; }
    }

    private sealed class TestAgentProxy : FabrCoreAgentProxy
    {
        public TestAgentProxy(
            AgentConfiguration config,
            IServiceProvider serviceProvider,
            IFabrCoreAgentHost fabrcoreAgentHost)
            : base(config, serviceProvider, fabrcoreAgentHost)
        {
        }

        public Task<T?> GetState<T>(string key) => GetStateAsync<T>(key);

        public Task<StateReadResult<T>> TryGetState<T>(string key) => TryGetStateAsync<T>(key);

        public void Set<T>(string key, T value) => SetState(key, value);

        public void Remove(string key) => RemoveState(key);

        public Task Flush() => FlushStateAsync();

        public bool Pending => HasPendingStateChanges;

        public AgentMessage? SentMessage => ((TestAgentHost)fabrcoreAgentHost).SentMessage;

        public Task SendText(
            string message,
            string? messageType = null,
            PrincipalDeliveryTarget? target = null) =>
            SendToUserAsync(message, messageType, target);

        public Task SendStructured(AgentMessage message) => SendToUserAsync(message);

        public IFabrCoreAgentHost Host => fabrcoreAgentHost;

        public Task<FabrCore.Connections.ConnectionAccessToken> Token() => Connections.GetAccessTokenAsync("background", "graph");

        public Task<HttpClient> Client() => Connections.GetHttpClientAsync("background", "graph");

        public override Task OnInitialize() => Task.CompletedTask;

        public override Task<AgentMessage> OnMessage(AgentMessage message) => Task.FromResult(message.Response());
    }

    private sealed class TestAgentHost : IFabrCoreAgentHost
    {
        private readonly string _handle;
        private Dictionary<string, JsonElement> _customState;

        public TestAgentHost(string handle, Dictionary<string, JsonElement> customState)
        {
            _handle = handle;
            _customState = customState;
        }

        public string GetHandle() => _handle;

        public AgentMessage? SentMessage { get; private set; }

        public void Clear() => SentMessage = null;

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request) => Task.FromResult(request.Response());

        public Task SendMessage(AgentMessage request)
        {
            SentMessage = request;
            return Task.CompletedTask;
        }

        public Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
            => Task.FromResult(new AgentHealthStatus
            {
                Handle = handle ?? _handle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true
            });

        public Task SendEvent(EventMessage request) => Task.CompletedTask;

        public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
        {
        }

        public void UnregisterTimer(string timerName)
        {
        }

        public Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
            => Task.CompletedTask;

        public Task UnregisterReminder(string reminderName) => Task.CompletedTask;

        public FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId)
            => throw new NotSupportedException();

        public void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider)
        {
        }

        public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId)
            => Task.FromResult(new List<StoredChatMessage>());

        public Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => Task.CompletedTask;

        public Task ClearThreadAsync(string threadId) => Task.CompletedTask;

        public Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => Task.CompletedTask;

        public Task<Dictionary<string, JsonElement>> GetCustomStateAsync() => Task.FromResult(_customState);

        public Func<Dictionary<string, JsonElement>, IEnumerable<string>, Task>? BeforeMerge { get; set; }

        public async Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
        {
            if (BeforeMerge is not null)
                await BeforeMerge(changes, deletes);

            foreach (var key in deletes)
            {
                _customState.Remove(key);
            }

            foreach (var (key, value) in changes)
            {
                _customState[key] = value;
            }

        }

        public void SetStatusMessage(string? message)
        {
        }
    }

    /// <summary>The host's rule in a test: each agent type's identity is the host's connection named for it.</summary>
    private sealed class TypeResolver : FabrCore.Connections.IConnectionBindingResolver
    {
        public FabrCore.Connections.ConnectionBinding? Resolve(FabrCore.Connections.ConnectionCaller caller, string connection)
            => new("host", "agentid-" + caller.AgentType);
    }

    /// <summary>Records who asked for which connection, through the overloads that carry the agent's type.</summary>
    private sealed class RecordingConnections : FabrCore.Connections.IConnectionService
    {
        public List<(FabrCore.Connections.ConnectionCaller Caller, FabrCore.Connections.ConnectionBinding Binding)> Calls { get; } = [];

        public Task<string> GetSessionVersionAsync(string principal, string agent, FabrCore.Connections.ConnectionBinding binding, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("The agent's type was dropped.");
        public Task<FabrCore.Connections.ConnectionAccessToken> GetAccessTokenAsync(string principal, string agent, FabrCore.Connections.ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("The agent's type was dropped.");
        public Task<HttpClient> GetHttpClientAsync(string principal, string agent, FabrCore.Connections.ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("The agent's type was dropped.");

        public Task<FabrCore.Connections.ConnectionAccessToken> GetAccessTokenAsync(FabrCore.Connections.ConnectionCaller caller, FabrCore.Connections.ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
        {
            Calls.Add((caller, binding));
            return Task.FromResult(new FabrCore.Connections.ConnectionAccessToken("t", DateTimeOffset.UtcNow.AddHours(1)));
        }
        public Task<HttpClient> GetHttpClientAsync(FabrCore.Connections.ConnectionCaller caller, FabrCore.Connections.ConnectionBinding binding, string resource, Func<string, Task> notify, CancellationToken cancellationToken = default)
        {
            Calls.Add((caller, binding));
            return Task.FromResult(new HttpClient());
        }
    }

    /// <summary>A connection whose sign-in lapsed: every token request fails with the notice the connection sends once.</summary>
    private sealed class LapsedConnections(FabrCore.Connections.ConnectionException lapse) : FabrCore.Connections.IConnectionService
    {
        public Task<string> GetSessionVersionAsync(string principal, string agent, FabrCore.Connections.ConnectionBinding binding, CancellationToken cancellationToken = default)
            => Task.FromResult("1");

        public Task<FabrCore.Connections.ConnectionAccessToken> GetAccessTokenAsync(string principal, string agent, FabrCore.Connections.ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
            => Task.FromException<FabrCore.Connections.ConnectionAccessToken>(lapse);

        public Task<HttpClient> GetHttpClientAsync(string principal, string agent, FabrCore.Connections.ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
            => GetHttpClientAsync(principal, agent, binding, resource, _ => Task.CompletedTask, cancellationToken);

        public Task<HttpClient> GetHttpClientAsync(string principal, string agent, FabrCore.Connections.ConnectionBinding binding, string resource, Func<string, Task> notify, CancellationToken cancellationToken = default)
            => Task.FromResult(new HttpClient(new Lapsing(lapse, notify)) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") });

        private sealed class Lapsing(FabrCore.Connections.ConnectionException lapse, Func<string, Task> notify) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await notify(lapse.Notice!);
                throw lapse;
            }
        }
    }

    private sealed class TestChatClientService : IFabrCoreChatClientService
    {
        public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
            => throw new NotSupportedException();

#pragma warning disable MEAI001
        public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
            => throw new NotSupportedException();
#pragma warning restore MEAI001

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
            => throw new NotSupportedException();

        public Task<ModelConfiguration> GetModelConfigurationAsync(string name)
            => Task.FromResult(new ModelConfiguration
            {
                Name = name,
                Provider = "Test",
                Uri = "http://localhost",
                Model = "test",
                ApiKeyAlias = "test"
            });
    }
}
