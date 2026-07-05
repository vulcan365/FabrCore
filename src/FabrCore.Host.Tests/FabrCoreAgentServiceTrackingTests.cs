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

    [TestMethod]
    public async Task GetAgentsAsync_ReturnsOnlyAgentEntries()
    {
        var service = CreateService(
            new Dictionary<string, FakeClientGrain>(),
            new FakeAgentManagementProvider(RegistryEntries()));

        var agents = await service.GetAgentsAsync();
        var activeAgents = await service.GetAgentsAsync("active");

        CollectionAssert.AreEquivalent(
            new[] { "user1:assistant", "user2:planner" },
            agents.Select(a => a.Key).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "user1:assistant" },
            activeAgents.Select(a => a.Key).ToArray());
        Assert.IsTrue(agents.All(a => a.EntityType == EntityType.Agent));
    }

    [TestMethod]
    public async Task GetUsersAsync_ReturnsOnlyClientEntries()
    {
        var service = CreateService(
            new Dictionary<string, FakeClientGrain>(),
            new FakeAgentManagementProvider(RegistryEntries()));

        var users = await service.GetUsersAsync();
        var deactivatedUsers = await service.GetUsersAsync("deactivated");

        CollectionAssert.AreEquivalent(
            new[] { "user1", "user2" },
            users.Select(u => u.Key).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "user2" },
            deactivatedUsers.Select(u => u.Key).ToArray());
        Assert.IsTrue(users.All(u => u.EntityType == EntityType.Client));
    }

    [TestMethod]
    public async Task EntitySpecificInfoMethods_DoNotCrossEntityTypes()
    {
        var service = CreateService(
            new Dictionary<string, FakeClientGrain>(),
            new FakeAgentManagementProvider(RegistryEntries()));

        Assert.IsNotNull(await service.GetAgentInfoAsync("user1:assistant"));
        Assert.IsNull(await service.GetAgentInfoAsync("user1"));
        Assert.IsNotNull(await service.GetUserInfoAsync("user1"));
        Assert.IsNull(await service.GetUserInfoAsync("user1:assistant"));
    }

    private static FabrCoreAgentService CreateService(Dictionary<string, FakeClientGrain> clientGrains)
        => CreateService(clientGrains, new FakeAgentManagementProvider());

    private static FabrCoreAgentService CreateService(
        Dictionary<string, FakeClientGrain> clientGrains,
        IAgentManagementProvider managementProvider)
    {
        var clusterClient = FakeClusterClientProxy.Create(clientGrains);
        return new FabrCoreAgentService(
            clusterClient,
            new FabrCoreRegistry(NullLogger<FabrCoreRegistry>.Instance),
            managementProvider,
            NullLogger<FabrCoreAgentService>.Instance);
    }

    private static List<AgentInfo> RegistryEntries() =>
    [
        NewInfo("user1:assistant", "test-agent", AgentStatus.Active, EntityType.Agent),
        NewInfo("user2:planner", "test-agent", AgentStatus.Deactivated, EntityType.Agent),
        NewInfo("user1", "Client", AgentStatus.Active, EntityType.Client),
        NewInfo("user2", "Client", AgentStatus.Deactivated, EntityType.Client)
    ];

    private static AgentInfo NewInfo(string key, string type, AgentStatus status, EntityType entityType) => new(
        Key: key,
        AgentType: type,
        Handle: key,
        Status: status,
        ActivatedAt: DateTime.UtcNow,
        DeactivatedAt: status == AgentStatus.Deactivated ? DateTime.UtcNow : null,
        DeactivationReason: status == AgentStatus.Deactivated ? "test" : null,
        EntityType: entityType);

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
        private readonly Dictionary<string, AgentInfo> _entries;

        public FakeAgentManagementProvider(IEnumerable<AgentInfo>? entries = null)
        {
            _entries = entries?.ToDictionary(e => e.Key, StringComparer.Ordinal)
                ?? new Dictionary<string, AgentInfo>(StringComparer.Ordinal);
        }

        public Task RegisterAgentAsync(string key, string agentType, string handle)
        {
            _entries[key] = NewInfo(key, agentType, AgentStatus.Active, EntityType.Agent);
            return Task.CompletedTask;
        }

        public Task DeactivateAgentAsync(string key, string reason)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = entry with
                {
                    Status = AgentStatus.Deactivated,
                    DeactivatedAt = DateTime.UtcNow,
                    DeactivationReason = reason
                };
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAgentAsync(string key) => Task.FromResult(_entries.Remove(key));

        public Task RegisterClientAsync(string clientId)
        {
            _entries[clientId] = NewInfo(clientId, "Client", AgentStatus.Active, EntityType.Client);
            return Task.CompletedTask;
        }

        public Task DeactivateClientAsync(string clientId, string reason)
        {
            if (_entries.TryGetValue(clientId, out var entry))
            {
                _entries[clientId] = entry with
                {
                    Status = AgentStatus.Deactivated,
                    DeactivatedAt = DateTime.UtcNow,
                    DeactivationReason = reason
                };
            }

            return Task.CompletedTask;
        }

        public Task<List<AgentInfo>> GetAllAsync() => Task.FromResult(_entries.Values.ToList());

        public Task<List<AgentInfo>> GetByStatusAsync(AgentStatus status)
            => Task.FromResult(_entries.Values.Where(e => e.Status == status).ToList());

        public Task<AgentInfo?> GetByKeyAsync(string key)
        {
            _entries.TryGetValue(key, out var entry);
            return Task.FromResult(entry);
        }

        public Task<List<AgentInfo>> GetByEntityTypeAsync(EntityType entityType, AgentStatus? status = null)
        {
            var entries = _entries.Values.Where(e => e.EntityType == entityType);
            if (status.HasValue)
            {
                entries = entries.Where(e => e.Status == status.Value);
            }

            return Task.FromResult(entries.ToList());
        }

        public Task<int> PurgeDeactivatedAsync(TimeSpan olderThan) => Task.FromResult(0);

        public Task<Dictionary<string, int>> GetStatisticsAsync() => Task.FromResult(new Dictionary<string, int>());
    }
}
