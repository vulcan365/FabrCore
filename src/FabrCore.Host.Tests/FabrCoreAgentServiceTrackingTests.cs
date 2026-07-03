using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using System.Reflection;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class FabrCoreAgentServiceTrackingTests
{
    [TestMethod]
    public async Task ConfigureAgentAsync_WithBareHandle_CreatesThroughClientGrainAndTracksAgent()
    {
        var clientGrain = new FakeClientGrain("user1");
        var service = CreateService(new Dictionary<string, FakeClientGrain>
        {
            ["user1"] = clientGrain
        });
        var config = NewConfig("assistant");

        var health = await service.ConfigureAgentAsync("user1", config);

        Assert.AreEqual("user1:assistant", health.Handle);
        Assert.IsTrue(await clientGrain.IsAgentTracked("assistant"));
        Assert.AreEqual("assistant", clientGrain.CreatedConfigs.Single().Handle);
        Assert.AreEqual("assistant", config.Handle, "ConfigureAgentAsync should not mutate the caller's config.");
    }

    [TestMethod]
    public async Task ConfigureAgentAsync_WithSameUserQualifiedHandle_DoesNotDoublePrefixAndTracksAgent()
    {
        var clientGrain = new FakeClientGrain("user1");
        var service = CreateService(new Dictionary<string, FakeClientGrain>
        {
            ["user1"] = clientGrain
        });

        var health = await service.ConfigureAgentAsync("user1", NewConfig("user1:assistant"));

        Assert.AreEqual("user1:assistant", health.Handle);
        Assert.IsTrue(await clientGrain.IsAgentTracked("assistant"));
        Assert.IsTrue(await clientGrain.IsAgentTracked("user1:assistant"));
        Assert.AreEqual("assistant", clientGrain.CreatedConfigs.Single().Handle);
    }

    [TestMethod]
    public async Task ConfigureAgentsAsync_TracksEverySuccessfulAgent()
    {
        var clientGrain = new FakeClientGrain("user1");
        var service = CreateService(new Dictionary<string, FakeClientGrain>
        {
            ["user1"] = clientGrain
        });

        var results = await service.ConfigureAgentsAsync("user1",
        [
            NewConfig("assistant"),
            NewConfig("planner")
        ]);

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(await clientGrain.IsAgentTracked("assistant"));
        Assert.IsTrue(await clientGrain.IsAgentTracked("planner"));
        CollectionAssert.AreEqual(
            new[] { "user1:assistant", "user1:planner" },
            clientGrain.TrackedHandles.ToArray());
    }

    [TestMethod]
    public async Task ConfigureSystemAgentAsync_TracksUnderSystemClientGrain()
    {
        var systemClientGrain = new FakeClientGrain("system");
        var service = CreateService(new Dictionary<string, FakeClientGrain>
        {
            ["system"] = systemClientGrain
        });

        var health = await service.ConfigureSystemAgentAsync(NewConfig("automation"));

        Assert.AreEqual("system:automation", health.Handle);
        Assert.IsTrue(await systemClientGrain.IsAgentTracked("automation"));
    }

    [TestMethod]
    public async Task ConfigureAgentAsync_WithDifferentUserQualifiedHandle_Throws()
    {
        var clientGrain = new FakeClientGrain("user1");
        var service = CreateService(new Dictionary<string, FakeClientGrain>
        {
            ["user1"] = clientGrain
        });

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.ConfigureAgentAsync("user1", NewConfig("user2:assistant")));

        Assert.AreEqual(0, clientGrain.CreatedConfigs.Count);
    }

    private static FabrCoreAgentService CreateService(Dictionary<string, FakeClientGrain> clientGrains)
    {
        var clusterClient = FakeClusterClientProxy.Create(clientGrains);
        return new FabrCoreAgentService(
            clusterClient,
            new FabrCoreRegistry(NullLogger<FabrCoreRegistry>.Instance),
            new FakeAgentManagementProvider(),
            NullLogger<FabrCoreAgentService>.Instance);
    }

    private static AgentConfiguration NewConfig(string handle) => new()
    {
        Handle = handle,
        AgentType = "test-agent",
        Models = "default",
        ForceReconfigure = true,
        Args = new Dictionary<string, string>
        {
            ["setting"] = "value"
        },
        Plugins = ["plugin"],
        Tools = ["tool"]
    };

    private class FakeClusterClientProxy : DispatchProxy
    {
        private Dictionary<string, FakeClientGrain> _clientGrains = new(StringComparer.Ordinal);

        public static IClusterClient Create(Dictionary<string, FakeClientGrain> clientGrains)
        {
            var proxy = DispatchProxy.Create<IClusterClient, FakeClusterClientProxy>();
            ((FakeClusterClientProxy)(object)proxy)._clientGrains = clientGrains;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain) &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments()[0] == typeof(IClientGrain) &&
                args is { Length: > 0 } &&
                args[0] is string userHandle &&
                _clientGrains.TryGetValue(userHandle, out var clientGrain))
            {
                return clientGrain;
            }

            throw new NotSupportedException($"Unexpected IClusterClient call: {targetMethod?.Name}");
        }
    }

    private sealed class FakeClientGrain : IClientGrain
    {
        private readonly string _userHandle;
        private readonly Dictionary<string, TrackedAgentInfo> _trackedAgents = new(StringComparer.Ordinal);

        public FakeClientGrain(string userHandle)
        {
            _userHandle = userHandle;
        }

        public List<AgentConfiguration> CreatedConfigs { get; } = new();

        public IReadOnlyList<string> TrackedHandles => _trackedAgents.Keys.ToList();

        public Task Subscribe(IClientGrainObserver observer) => throw new NotSupportedException();

        public Task Unsubscribe(IClientGrainObserver observer) => throw new NotSupportedException();

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request) => throw new NotSupportedException();

        public Task SendMessage(AgentMessage request) => throw new NotSupportedException();

        public Task SendEvent(EventMessage request) => throw new NotSupportedException();

        public Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
        {
            CreatedConfigs.Add(agentConfiguration);

            var fullHandle = HandleUtilities.EnsurePrefix(
                agentConfiguration.Handle ?? throw new ArgumentException("Handle is required.", nameof(agentConfiguration)),
                HandleUtilities.BuildPrefix(_userHandle));

            _trackedAgents[fullHandle] = new TrackedAgentInfo(fullHandle, agentConfiguration.AgentType ?? "Unknown");

            return Task.FromResult(new AgentHealthStatus
            {
                Handle = fullHandle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true,
                AgentType = agentConfiguration.AgentType
            });
        }

        public Task<AgentHealthStatus> ResetAgent(string handle) => throw new NotSupportedException();

        public Task<bool> UntrackAgent(string handle) => throw new NotSupportedException();

        public Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false)
            => Task.FromResult(_trackedAgents.Values.ToList());

        public Task<bool> IsAgentTracked(string handle)
        {
            var fullHandle = HandleUtilities.EnsurePrefix(handle, HandleUtilities.BuildPrefix(_userHandle));
            return Task.FromResult(_trackedAgents.ContainsKey(fullHandle));
        }

        public Task<List<AgentInfo>> GetAccessibleSharedAgents() => throw new NotSupportedException();
    }

    private sealed class FakeAgentManagementProvider : IAgentManagementProvider
    {
        public Task RegisterAgentAsync(string key, string agentType, string handle) => Task.CompletedTask;

        public Task DeactivateAgentAsync(string key, string reason) => Task.CompletedTask;

        public Task<bool> RemoveAgentAsync(string key) => Task.FromResult(false);

        public Task RegisterClientAsync(string clientId) => Task.CompletedTask;

        public Task DeactivateClientAsync(string clientId, string reason) => Task.CompletedTask;

        public Task<List<AgentInfo>> GetAllAsync() => Task.FromResult(new List<AgentInfo>());

        public Task<List<AgentInfo>> GetByStatusAsync(AgentStatus status) => Task.FromResult(new List<AgentInfo>());

        public Task<AgentInfo?> GetByKeyAsync(string key) => Task.FromResult<AgentInfo?>(null);

        public Task<List<AgentInfo>> GetByEntityTypeAsync(EntityType entityType, AgentStatus? status = null)
            => Task.FromResult(new List<AgentInfo>());

        public Task<int> PurgeDeactivatedAsync(TimeSpan olderThan) => Task.FromResult(0);

        public Task<Dictionary<string, int>> GetStatisticsAsync() => Task.FromResult(new Dictionary<string, int>());
    }
}
