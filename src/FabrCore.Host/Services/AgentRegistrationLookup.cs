using FabrCore.Core.Interfaces;
using Orleans;

namespace FabrCore.Host.Services;

/// <summary>Resolves the durable registration of an agent without activating the agent itself.</summary>
public static class AgentRegistrationLookup
{
    public static Task<Guid?> GetRegistrationAsync(IClusterClient cluster, string principal, string fullHandle)
        => cluster.GetGrain<IPrincipalGrain>(principal).GetAgentRegistrationId(fullHandle);
}
