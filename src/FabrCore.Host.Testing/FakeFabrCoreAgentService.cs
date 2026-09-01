using System.Text.Json;
using FabrCore.Core;
using FabrCore.Host.Services;
using FabrCore.Sdk;

namespace FabrCore.Host.Testing;

/// <summary>
/// An <see cref="IFabrCoreAgentService"/> that records what it was asked and answers with a canned
/// reply, so a test can assert which principal and handle a call reached without an Orleans silo.
/// </summary>
/// <remarks>
/// Only the members the A2A endpoints use are implemented. The rest throw, which is deliberate: a
/// test that trips one is exercising a path this fake was never meant to stand in for.
/// </remarks>
public sealed class FakeFabrCoreAgentService : IFabrCoreAgentService
{
    /// <summary>Every message sent, with the principal and handle it was addressed to.</summary>
    public List<SentMessage> Sends { get; } = new();

    /// <summary>Every provisioning request, with the principal it was made for.</summary>
    public List<EnsuredAgents> Ensured { get; } = new();

    /// <summary>Reply text returned for every request. Ignored when <see cref="ReplyFactory"/> is set.</summary>
    public string Reply { get; set; } = "ok";

    /// <summary>Full control over the reply, for error, delay, and cancellation scenarios.</summary>
    public Func<AgentMessage, Task<AgentMessage>>? ReplyFactory { get; set; }

    /// <summary>Agents the cluster reports as live, for <c>A2A:Discovery:IncludeAgentHandles</c>.</summary>
    public List<AgentInfo> LiveAgents { get; } = new();

    /// <summary>How many times live-agent discovery hit the cluster, for cache assertions.</summary>
    public int GetAgentsCalls { get; private set; }

    /// <summary>Adds a live agent using the <c>principal:handle</c> key shape the host uses.</summary>
    public FakeFabrCoreAgentService WithLiveAgent(string key, string agentType = "chat-agent")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var handle = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
        LiveAgents.Add(new AgentInfo(
            key, agentType, handle, AgentStatus.Active, DateTime.UtcNow, null, null, EntityType.Agent));
        return this;
    }

    /// <summary>A message the fake received.</summary>
    public sealed record SentMessage(string Principal, string Handle, AgentMessage Message);

    /// <summary>A provisioning request the fake received.</summary>
    public sealed record EnsuredAgents(string Principal, IReadOnlyList<AgentConfiguration> Configs);

    public Task<List<AgentHealthStatus>> EnsureAgentsAsync(
        string userHandle, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
    {
        Ensured.Add(new EnsuredAgents(userHandle, configs));
        return Task.FromResult(configs.Select(c => new AgentHealthStatus
        {
            Handle = c.Handle!,
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true,
        }).ToList());
    }

    public async Task<AgentMessage> SendAndReceiveMessageAsync(string userHandle, string handle, AgentMessage message)
    {
        Sends.Add(new SentMessage(userHandle, handle, message));
        if (ReplyFactory is not null)
        {
            return await ReplyFactory(message);
        }

        return new AgentMessage { Message = Reply, Kind = MessageKind.Response };
    }

    public Task<AgentMessage> SendAndReceiveMessageAsync(string userHandle, string handle, string message)
        => SendAndReceiveMessageAsync(userHandle, handle, new AgentMessage { Message = message });

    public Task<List<AgentInfo>> GetAgentsAsync(string? status = null)
    {
        GetAgentsCalls++;
        return Task.FromResult(LiveAgents.ToList());
    }

    // ── Not used by the A2A endpoints ──────────────────────────────────────────────────────

    public Task<AgentHealthStatus> ConfigureAgentAsync(string userHandle, AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic) => throw new NotSupportedException();
    public Task<AgentHealthStatus> ConfigureSystemAgentAsync(AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic) => throw new NotSupportedException();
    public Task<List<AgentHealthStatus>> ConfigureAgentsAsync(string userHandle, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic) => throw new NotSupportedException();
    public Task SendMessageAsync(string userHandle, string handle, string message) => throw new NotSupportedException();
    public Task SendMessageAsync(string userHandle, string handle, AgentMessage message) => throw new NotSupportedException();
    public Task<AgentHealthStatus> GetHealthAsync(string userHandle, string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic) => throw new NotSupportedException();
    public Task<AgentEvictionResult> EvictAgentAsync(string userHandle, string handle) => throw new NotSupportedException();
    public Task SendEventAsync(string userHandle, string handle, EventMessage message) => throw new NotSupportedException();
    public Task RegisterAgentAsync(string key, string agentType, string handle) => throw new NotSupportedException();
    public Task DeactivateAgentAsync(string key, string reason) => throw new NotSupportedException();
    public Task<bool> RemoveAgentAsync(string key) => throw new NotSupportedException();
    public Task RegisterPrincipalAsync(string principalHandle) => throw new NotSupportedException();
    public Task DeactivatePrincipalAsync(string principalHandle, string reason) => throw new NotSupportedException();
    public Task<AgentInfo?> GetAgentInfoAsync(string key) => throw new NotSupportedException();
    public Task<List<AgentInfo>> GetPrincipalsAsync(string? status = null) => throw new NotSupportedException();
    public Task<AgentInfo?> GetPrincipalInfoAsync(string handle) => throw new NotSupportedException();
    public Task<Dictionary<string, int>> GetAgentStatisticsAsync() => throw new NotSupportedException();
    public Task<int> PurgeDeactivatedAgentsAsync(TimeSpan olderThan) => throw new NotSupportedException();
    public Task<List<AgentInfo>> GetAgentsByEntityTypeAsync(EntityType entityType) => throw new NotSupportedException();
    public List<RegistryEntry> GetAgentTypes() => throw new NotSupportedException();
    public List<RegistryEntry> GetPlugins() => throw new NotSupportedException();
    public List<RegistryEntry> GetTools() => throw new NotSupportedException();
    public List<RegistryCollision> GetCollisions() => throw new NotSupportedException();
    public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string userHandle, string handle, string threadId) => throw new NotSupportedException();
    public Task AddThreadMessagesAsync(string userHandle, string handle, string threadId, IEnumerable<StoredChatMessage> messages) => throw new NotSupportedException();
    public Task ClearThreadMessagesAsync(string userHandle, string handle, string threadId) => throw new NotSupportedException();
    public Task ReplaceThreadMessagesAsync(string userHandle, string handle, string threadId, IEnumerable<StoredChatMessage> messages) => throw new NotSupportedException();
    public Task<Dictionary<string, JsonElement>> GetCustomStateAsync(string userHandle, string handle) => throw new NotSupportedException();
    public Task MergeCustomStateAsync(string userHandle, string handle, Dictionary<string, JsonElement> changes, IEnumerable<string> deletes) => throw new NotSupportedException();
}

/// <summary>
/// An <see cref="IFabrCoreRegistry"/> whose agent types are supplied by the test, for exercising
/// <c>A2A:Discovery</c> without loading real agent assemblies.
/// </summary>
public sealed class FakeFabrCoreRegistry : IFabrCoreRegistry
{
    private readonly List<RegistryEntry> _agentTypes = new();

    /// <summary>Adds an agent type as the registry would report it.</summary>
    /// <param name="alias">The <c>[AgentAlias]</c> value.</param>
    /// <param name="description">The <c>[Description]</c>; required for <c>Described</c> discovery.</param>
    /// <param name="capabilities">The <c>[FabrCoreCapabilities]</c>, comma-separated; becomes card tags.</param>
    /// <param name="notes">The <c>[FabrCoreNote]</c> values; become card skill examples.</param>
    public FakeFabrCoreRegistry WithAgentType(
        string alias, string? description = null, string? capabilities = null, params string[] notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        _agentTypes.Add(new RegistryEntry
        {
            TypeName = alias,
            Aliases = [alias],
            Description = description,
            Capabilities = capabilities,
            Notes = [.. notes],
        });
        return this;
    }

    public List<RegistryEntry> GetAgentTypes() => [.. _agentTypes];

    public List<RegistryEntry> GetPlugins() => [];

    public List<RegistryEntry> GetTools() => [];

    public List<RegistryCollision> GetCollisions() => [];

    public Type? FindAgentType(string alias) => null;
}
