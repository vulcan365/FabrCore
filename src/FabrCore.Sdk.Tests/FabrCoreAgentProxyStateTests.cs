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

    private static TestAgentProxy CreateAgent(Dictionary<string, JsonElement> customState, FabrCore.Connections.IConnectionService? connections = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IConfiguration>(new ConfigurationManager());
        services.AddSingleton<IFabrCoreChatClientService, TestChatClientService>();
        if (connections is not null)
            services.AddSingleton(connections);
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

        public Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
        {
            foreach (var key in deletes)
            {
                _customState.Remove(key);
            }

            foreach (var (key, value) in changes)
            {
                _customState[key] = value;
            }

            return Task.CompletedTask;
        }

        public void SetStatusMessage(string? message)
        {
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
