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
    public async Task ConfigureAgentAsync_WithBareHandle_CreatesThroughPrincipalGrainAndTracksAgent()
    {
        var principalGrain = new FakePrincipalGrain("user1");
        var service = CreateService(new Dictionary<string, FakePrincipalGrain>
        {
            ["user1"] = principalGrain
        });
        var config = NewConfig("assistant");

        var health = await service.ConfigureAgentAsync("user1", config);

        Assert.AreEqual("user1:assistant", health.Handle);
        Assert.IsTrue(await principalGrain.IsAgentTracked("assistant"));
        Assert.AreEqual("assistant", principalGrain.CreatedConfigs.Single().Handle);
        Assert.AreEqual("assistant", config.Handle, "ConfigureAgentAsync should not mutate the caller's config.");
    }

    [TestMethod]
    public async Task ConfigureAgentAsync_WithSameUserQualifiedHandle_DoesNotDoublePrefixAndTracksAgent()
    {
        var principalGrain = new FakePrincipalGrain("user1");
        var service = CreateService(new Dictionary<string, FakePrincipalGrain>
        {
            ["user1"] = principalGrain
        });

        var health = await service.ConfigureAgentAsync("user1", NewConfig("user1:assistant"));

        Assert.AreEqual("user1:assistant", health.Handle);
        Assert.IsTrue(await principalGrain.IsAgentTracked("assistant"));
        Assert.IsTrue(await principalGrain.IsAgentTracked("user1:assistant"));
        Assert.AreEqual("assistant", principalGrain.CreatedConfigs.Single().Handle);
    }

    [TestMethod]
    public async Task ConfigureAgentsAsync_TracksEverySuccessfulAgent()
    {
        var principalGrain = new FakePrincipalGrain("user1");
        var service = CreateService(new Dictionary<string, FakePrincipalGrain>
        {
            ["user1"] = principalGrain
        });

        var results = await service.ConfigureAgentsAsync("user1",
        [
            NewConfig("assistant"),
            NewConfig("planner")
        ]);

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(await principalGrain.IsAgentTracked("assistant"));
        Assert.IsTrue(await principalGrain.IsAgentTracked("planner"));
        CollectionAssert.AreEqual(
            new[] { "user1:assistant", "user1:planner" },
            principalGrain.TrackedHandles.ToArray());
    }

    [TestMethod]
    public async Task ConfigureSystemAgentAsync_TracksUnderSystemPrincipalGrain()
    {
        var systemPrincipalGrain = new FakePrincipalGrain("system");
        var service = CreateService(new Dictionary<string, FakePrincipalGrain>
        {
            ["system"] = systemPrincipalGrain
        });

        var health = await service.ConfigureSystemAgentAsync(NewConfig("automation"));

        Assert.AreEqual("system:automation", health.Handle);
        Assert.IsTrue(await systemPrincipalGrain.IsAgentTracked("automation"));
    }

    [TestMethod]
    public async Task ConfigureAgentAsync_WithDifferentUserQualifiedHandle_Throws()
    {
        var principalGrain = new FakePrincipalGrain("user1");
        var service = CreateService(new Dictionary<string, FakePrincipalGrain>
        {
            ["user1"] = principalGrain
        });

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.ConfigureAgentAsync("user1", NewConfig("user2:assistant")));

        Assert.AreEqual(0, principalGrain.CreatedConfigs.Count);
    }

    [TestMethod]
    public async Task GetAgentsAsync_ReturnsOnlyAgentEntries()
    {
        var service = CreateService(
            new Dictionary<string, FakePrincipalGrain>(),
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
    public async Task GetPrincipalsAsync_ReturnsOnlyPrincipalEntries()
    {
        var service = CreateService(
            new Dictionary<string, FakePrincipalGrain>(),
            new FakeAgentManagementProvider(RegistryEntries()));

        var principals = await service.GetPrincipalsAsync();
        var deactivatedPrincipals = await service.GetPrincipalsAsync("deactivated");

        CollectionAssert.AreEquivalent(
            new[] { "user1", "user2" },
            principals.Select(p => p.Key).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "user2" },
            deactivatedPrincipals.Select(p => p.Key).ToArray());
        Assert.IsTrue(principals.All(p => p.EntityType == EntityType.Principal));
    }

    [TestMethod]
    public async Task EntitySpecificInfoMethods_DoNotCrossEntityTypes()
    {
        var service = CreateService(
            new Dictionary<string, FakePrincipalGrain>(),
            new FakeAgentManagementProvider(RegistryEntries()));

        Assert.IsNotNull(await service.GetAgentInfoAsync("user1:assistant"));
        Assert.IsNull(await service.GetAgentInfoAsync("user1"));
        Assert.IsNotNull(await service.GetPrincipalInfoAsync("user1"));
        Assert.IsNull(await service.GetPrincipalInfoAsync("user1:assistant"));
    }

    private static FabrCoreAgentService CreateService(Dictionary<string, FakePrincipalGrain> principalGrains)
        => CreateService(principalGrains, new FakeAgentManagementProvider());

    private static FabrCoreAgentService CreateService(
        Dictionary<string, FakePrincipalGrain> principalGrains,
        IAgentManagementProvider managementProvider)
    {
        var clusterClient = FakeClusterClientProxy.Create(principalGrains);
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
        NewInfo("user1", "Principal", AgentStatus.Active, EntityType.Principal),
        NewInfo("user2", "Principal", AgentStatus.Deactivated, EntityType.Principal)
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
        private Dictionary<string, FakePrincipalGrain> _principalGrains = new(StringComparer.Ordinal);

        public static IClusterClient Create(Dictionary<string, FakePrincipalGrain> principalGrains)
        {
            var proxy = DispatchProxy.Create<IClusterClient, FakeClusterClientProxy>();
            ((FakeClusterClientProxy)(object)proxy)._principalGrains = principalGrains;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain) &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments()[0] == typeof(IPrincipalGrain) &&
                args is { Length: > 0 } &&
                args[0] is string principalHandle &&
                _principalGrains.TryGetValue(principalHandle, out var principalGrain))
            {
                return principalGrain;
            }

            throw new NotSupportedException($"Unexpected IClusterClient call: {targetMethod?.Name}");
        }
    }

    private sealed class FakePrincipalGrain : IPrincipalGrain
    {
        private readonly string _principalHandle;
        private readonly Dictionary<string, TrackedAgentInfo> _trackedAgents = new(StringComparer.Ordinal);

        public FakePrincipalGrain(string principalHandle)
        {
            _principalHandle = principalHandle;
        }

        public List<AgentConfiguration> CreatedConfigs { get; } = new();

        public IReadOnlyList<string> TrackedHandles => _trackedAgents.Keys.ToList();

        public Task Subscribe(IPrincipalGrainObserver observer) => throw new NotSupportedException();

        public Task Unsubscribe(IPrincipalGrainObserver observer) => throw new NotSupportedException();

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request) => throw new NotSupportedException();

        public Task SendMessage(AgentMessage request) => throw new NotSupportedException();

        public Task SendEvent(EventMessage request) => throw new NotSupportedException();

        public Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
        {
            CreatedConfigs.Add(agentConfiguration);

            var fullHandle = HandleUtilities.EnsurePrefix(
                agentConfiguration.Handle ?? throw new ArgumentException("Handle is required.", nameof(agentConfiguration)),
                HandleUtilities.BuildPrefix(_principalHandle));

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
            var fullHandle = HandleUtilities.EnsurePrefix(handle, HandleUtilities.BuildPrefix(_principalHandle));
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

        public Task RegisterPrincipalAsync(string principalHandle)
        {
            _entries[principalHandle] = NewInfo(principalHandle, "Principal", AgentStatus.Active, EntityType.Principal);
            return Task.CompletedTask;
        }

        public Task DeactivatePrincipalAsync(string principalHandle, string reason)
        {
            if (_entries.TryGetValue(principalHandle, out var entry))
            {
                _entries[principalHandle] = entry with
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
