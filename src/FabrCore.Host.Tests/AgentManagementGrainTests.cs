using FabrCore.Core;
using FabrCore.Host.Grains;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class AgentManagementGrainTests
{
    [TestMethod]
    public async Task PurgeDeactivatedAgentsOlderThan_RemovesOnlyAgentEntries()
    {
        var oldDeactivatedAt = DateTime.UtcNow.AddDays(-10);
        var state = new FakePersistentState<Dictionary<string, AgentInfo>>(new Dictionary<string, AgentInfo>
        {
            ["user1:old-agent"] = NewInfo("user1:old-agent", "test-agent", AgentStatus.Deactivated, EntityType.Agent, oldDeactivatedAt),
            ["user2:active-agent"] = NewInfo("user2:active-agent", "test-agent", AgentStatus.Active, EntityType.Agent, null),
            ["user1"] = NewInfo("user1", "Principal", AgentStatus.Deactivated, EntityType.Principal, oldDeactivatedAt),
            ["user2"] = NewInfo("user2", "Principal", AgentStatus.Active, EntityType.Principal, null)
        });
        var grain = new AgentManagementGrain(state, NullLogger<AgentManagementGrain>.Instance);

        var purged = await grain.PurgeDeactivatedAgentsOlderThan(TimeSpan.FromDays(7));

        Assert.AreEqual(1, purged);
        Assert.IsFalse(state.State.ContainsKey("user1:old-agent"));
        Assert.IsTrue(state.State.ContainsKey("user2:active-agent"));
        Assert.IsTrue(state.State.ContainsKey("user1"));
        Assert.IsTrue(state.State.ContainsKey("user2"));
        Assert.AreEqual(1, state.WriteCount);
    }

    private static AgentInfo NewInfo(
        string key,
        string type,
        AgentStatus status,
        EntityType entityType,
        DateTime? deactivatedAt) => new(
            Key: key,
            AgentType: type,
            Handle: key,
            Status: status,
            ActivatedAt: DateTime.UtcNow.AddDays(-12),
            DeactivatedAt: deactivatedAt,
            DeactivationReason: deactivatedAt.HasValue ? "test" : null,
            EntityType: entityType);

    private sealed class FakePersistentState<T> : IPersistentState<T>
    {
        public FakePersistentState(T state)
        {
            State = state;
        }

        public T State { get; set; }

        public string Etag { get; set; } = string.Empty;

        public bool RecordExists { get; set; } = true;

        public int WriteCount { get; private set; }

        public Task ClearStateAsync()
        {
            State = default!;
            return Task.CompletedTask;
        }

        public Task WriteStateAsync()
        {
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task ReadStateAsync() => Task.CompletedTask;
    }
}
