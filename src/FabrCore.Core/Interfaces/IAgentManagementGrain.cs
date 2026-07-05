using Orleans;

namespace FabrCore.Core.Interfaces
{
    public interface IAgentManagementGrain : IGrainWithIntegerKey
    {
        // Agent Registration
        Task RegisterAgent(string key, string agentType, string handle);
        Task DeactivateAgent(string key, string reason);
        Task<bool> RemoveAgent(string key);

        // Principal Registration
        Task RegisterPrincipal(string principalHandle);
        Task DeactivatePrincipal(string principalHandle, string reason);

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
