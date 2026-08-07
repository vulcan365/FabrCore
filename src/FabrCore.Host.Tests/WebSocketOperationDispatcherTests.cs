using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Core.WebSockets;
using FabrCore.Host.WebSocket;
using Orleans.Concurrency;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class WebSocketOperationDispatcherTests
{
    [TestMethod]
    public async Task AsyncRequestKind_UsesSendMessageAndOverwritesSender()
    {
        var principal = new FakePrincipalGrain();
        var dispatcher = new WebSocketOperationDispatcher(principal, "alice");
        var frame = Request(FabrCoreWebSocketOperations.MessageSend,
            new AgentMessage { Kind = MessageKind.Request, FromHandle = "forged", ToHandle = "assistant" });
        frame.DeliveryMode = FabrCoreWebSocketDeliveryModes.Async;

        var result = await dispatcher.DispatchAsync(frame);

        Assert.IsInstanceOfType<FabrCoreWebSocketAccepted>(result);
        Assert.AreEqual(1, principal.SendCount);
        Assert.AreEqual(0, principal.SendAndReceiveCount);
        Assert.AreEqual("alice", principal.LastMessage!.FromHandle);
    }

    [TestMethod]
    public async Task RequestResponseOneWay_UsesSendAndReceive()
    {
        var principal = new FakePrincipalGrain();
        var dispatcher = new WebSocketOperationDispatcher(principal, "alice");
        var frame = Request(FabrCoreWebSocketOperations.MessageSend,
            new AgentMessage { Kind = MessageKind.OneWay, ToHandle = "assistant" });
        frame.DeliveryMode = FabrCoreWebSocketDeliveryModes.RequestResponse;

        var result = await dispatcher.DispatchAsync(frame);

        Assert.IsInstanceOfType<AgentMessage>(result);
        Assert.AreEqual(0, principal.SendCount);
        Assert.AreEqual(1, principal.SendAndReceiveCount);
    }

    [TestMethod]
    public async Task EventSourceIsOverwritten()
    {
        var principal = new FakePrincipalGrain();
        var dispatcher = new WebSocketOperationDispatcher(principal, "alice");

        await dispatcher.DispatchAsync(Request(FabrCoreWebSocketOperations.EventSend,
            new EventMessage { Source = "forged", Channel = "assistant" }));

        Assert.AreEqual("alice", principal.LastEvent!.Source);
    }

    [TestMethod]
    public async Task CreationAndLegacyCreationAreUnsupported()
    {
        var dispatcher = new WebSocketOperationDispatcher(new FakePrincipalGrain(), "alice");
        await Assert.ThrowsExactlyAsync<UnsupportedWebSocketOperationException>(() =>
            dispatcher.DispatchAsync(Request("agent.create", new { })));
        await Assert.ThrowsExactlyAsync<UnsupportedWebSocketOperationException>(() =>
            dispatcher.DispatchAsync(Request("createagent", new { })));
    }

    [TestMethod]
    public void LongMessageAndAckCallsAreInterleavable()
    {
        var interfaceType = typeof(IPrincipalGrain);
        Assert.IsNotNull(interfaceType.GetMethod(nameof(IPrincipalGrain.SendAndReceiveMessage))!
            .GetCustomAttributes(typeof(AlwaysInterleaveAttribute), false).SingleOrDefault());
        Assert.IsNotNull(interfaceType.GetMethod(nameof(IPrincipalGrain.AcknowledgeWebSocket))!
            .GetCustomAttributes(typeof(AlwaysInterleaveAttribute), false).SingleOrDefault());
    }

    private static FabrCoreWebSocketFrame Request(string operation, object payload)
    {
        var frame = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Request, payload);
        frame.Id = Guid.NewGuid().ToString("N");
        frame.Operation = operation;
        return frame;
    }

    private sealed class FakePrincipalGrain : IPrincipalGrain
    {
        public int SendCount { get; private set; }
        public int SendAndReceiveCount { get; private set; }
        public AgentMessage? LastMessage { get; private set; }
        public EventMessage? LastEvent { get; private set; }

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            SendAndReceiveCount++;
            LastMessage = request;
            return Task.FromResult(new AgentMessage { Kind = MessageKind.Response });
        }

        public Task SendMessage(AgentMessage request) { SendCount++; LastMessage = request; return Task.CompletedTask; }
        public Task SendEvent(EventMessage request) { LastEvent = request; return Task.CompletedTask; }
        public Task<AgentHealthStatus> ResetAgent(string handle) => Task.FromResult(Health(handle));
        public Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic) => Task.FromResult(Health(handle));
        public Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false) => Task.FromResult(new List<TrackedAgentInfo>());
        public Task<bool> IsAgentTracked(string handle) => Task.FromResult(true);
        public Task<List<AgentInfo>> GetAccessibleSharedAgents() => Task.FromResult(new List<AgentInfo>());
        public Task Subscribe(IPrincipalGrainObserver observer) => throw new NotSupportedException();
        public Task Unsubscribe(IPrincipalGrainObserver observer) => throw new NotSupportedException();
        public Task<FabrCoreWebSocketRegistration> SubscribeWebSocket(IPrincipalWebSocketObserver observer, string clientId, string connectionId, long? checkpoint) => throw new NotSupportedException();
        public Task UnsubscribeWebSocket(string clientId, string connectionId) => Task.CompletedTask;
        public Task AcknowledgeWebSocket(string clientId, long sequence) => Task.CompletedTask;
        public Task SetContextValue(string key, string? value) => throw new NotSupportedException();
        public Task<string?> GetContextValue(string key) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetContextValues() => throw new NotSupportedException();
        public Task CompletePrincipalMessageDelivery(string deliveryId, PrincipalMessageDeliveryOutcome outcome) => throw new NotSupportedException();
        public Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration) => throw new AssertFailedException("Creation must never be dispatched over WebSocket.");
        public Task<bool> UntrackAgent(string handle) => throw new NotSupportedException();

        private static AgentHealthStatus Health(string handle) => new()
        {
            Handle = handle,
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true,
        };
    }
}
