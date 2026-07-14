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

    private static TestAgentProxy CreateAgent(Dictionary<string, JsonElement> customState)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IConfiguration>(new ConfigurationManager());
        services.AddSingleton<IFabrCoreChatClientService, TestChatClientService>();
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
