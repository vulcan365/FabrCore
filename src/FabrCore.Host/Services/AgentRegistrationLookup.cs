using FabrCore.Core.Interfaces;
using FabrCore.Core;
using Orleans;

namespace FabrCore.Host.Services;

/// <summary>Resolves the durable registration of an agent without activating the agent itself.</summary>
public static class AgentRegistrationLookup
{
    public static Task<Guid?> GetRegistrationAsync(IClusterClient cluster, string principal, string fullHandle)
        => cluster.GetGrain<IPrincipalGrain>(principal).GetAgentRegistrationId(fullHandle);

    /// <summary>Returns registered handles and their host-recorded types without activating agents.</summary>
    public static Task<List<TrackedAgentInfo>> GetTrackedAsync(IClusterClient cluster, string principal)
        => cluster.GetGrain<IPrincipalGrain>(principal).GetTrackedAgents();
}
