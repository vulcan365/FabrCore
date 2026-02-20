using Orleans;

namespace Fabr.Core.Interfaces
{
    public interface IAgentManagementGrain : IGrainWithIntegerKey
    {
        // Agent Registration
        Task RegisterAgent(string key, string agentType, string handle);
        Task DeactivateAgent(string key, string reason);

        // Client Registration
        Task RegisterClient(string clientId);
        Task DeactivateClient(string clientId, string reason);

        // Queries
        Task<List<AgentInfo>> GetAllAgents();
        Task<List<AgentInfo>> GetActiveAgents();
        Task<List<AgentInfo>> GetDeactivatedAgents();
        Task<AgentInfo?> GetAgentInfo(string key);

        // Entity type filtering
        Task<List<AgentInfo>> GetAllByEntityType(EntityType entityType);
        Task<List<AgentInfo>> GetActiveByEntityType(EntityType entityType);

        // History management
        Task<int> PurgeDeactivatedAgentsOlderThan(TimeSpan age);
        Task<Dictionary<string, int>> GetAgentStatistics();
    }
}
