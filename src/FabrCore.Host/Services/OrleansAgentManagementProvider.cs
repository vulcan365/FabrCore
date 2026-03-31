using FabrCore.Core;
using FabrCore.Core.Interfaces;
using Orleans;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Default <see cref="IAgentManagementProvider"/> implementation that delegates to the
    /// <see cref="IAgentManagementGrain"/> Orleans grain. This preserves the original
    /// single-grain registry behavior and is used unless overridden via
    /// <c>FabrCoreServerOptions.UseAgentManagementProvider&lt;T&gt;()</c>.
    /// </summary>
    public class OrleansAgentManagementProvider : IAgentManagementProvider
    {
        private readonly IClusterClient _clusterClient;

        public OrleansAgentManagementProvider(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient;
        }

        private IAgentManagementGrain GetGrain() => _clusterClient.GetGrain<IAgentManagementGrain>(0);

        // ── Registration ──

        public Task RegisterAgentAsync(string key, string agentType, string handle)
            => GetGrain().RegisterAgent(key, agentType, handle);

        public Task DeactivateAgentAsync(string key, string reason)
            => GetGrain().DeactivateAgent(key, reason);

        public Task RegisterClientAsync(string clientId)
            => GetGrain().RegisterClient(clientId);

        public Task DeactivateClientAsync(string clientId, string reason)
            => GetGrain().DeactivateClient(clientId, reason);

        // ── Queries ──

        public Task<List<AgentInfo>> GetAllAsync()
            => GetGrain().GetAllAgents();

        public async Task<List<AgentInfo>> GetByStatusAsync(AgentStatus status)
        {
            return status switch
            {
                AgentStatus.Active => await GetGrain().GetActiveAgents(),
                AgentStatus.Deactivated => await GetGrain().GetDeactivatedAgents(),
                _ => await GetGrain().GetAllAgents()
            };
        }

        public Task<AgentInfo?> GetByKeyAsync(string key)
            => GetGrain().GetAgentInfo(key);

        public async Task<List<AgentInfo>> GetByEntityTypeAsync(EntityType entityType, AgentStatus? status = null)
        {
            if (status.HasValue && status.Value == AgentStatus.Active)
                return await GetGrain().GetActiveByEntityType(entityType);

            return await GetGrain().GetAllByEntityType(entityType);
        }

        // ── Maintenance ──

        public Task<int> PurgeDeactivatedAsync(TimeSpan olderThan)
            => GetGrain().PurgeDeactivatedAgentsOlderThan(olderThan);

        public Task<Dictionary<string, int>> GetStatisticsAsync()
            => GetGrain().GetAgentStatistics();
    }
}
