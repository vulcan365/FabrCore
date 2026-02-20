using FabrCore.Core;
using FabrCore.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace FabrCore.Host.Grains
{
    public class AgentManagementGrain : Grain, IAgentManagementGrain
    {
        private readonly IPersistentState<Dictionary<string, AgentInfo>> _state;
        private readonly ILogger<AgentManagementGrain> _logger;

        public AgentManagementGrain(
            [PersistentState("agentRegistry", "fabrcoreStorage")]
            IPersistentState<Dictionary<string, AgentInfo>> state,
            ILogger<AgentManagementGrain> logger)
        {
            _state = state;
            _logger = logger;
        }

        public async Task RegisterAgent(string key, string agentType, string handle)
        {
            _logger.LogInformation("Registering agent: {Key}, Type: {AgentType}, Handle: {Handle}",
                key, agentType, handle);

            _state.State[key] = new AgentInfo(
                Key: key,
                AgentType: agentType,
                Handle: handle,
                Status: AgentStatus.Active,
                ActivatedAt: DateTime.UtcNow,
                DeactivatedAt: null,
                DeactivationReason: null,
                EntityType: EntityType.Agent
            );

            await _state.WriteStateAsync();
        }

        public async Task RegisterClient(string clientId)
        {
            _logger.LogInformation("Registering client: {ClientId}", clientId);

            _state.State[clientId] = new AgentInfo(
                Key: clientId,
                AgentType: "Client",
                Handle: clientId,
                Status: AgentStatus.Active,
                ActivatedAt: DateTime.UtcNow,
                DeactivatedAt: null,
                DeactivationReason: null,
                EntityType: EntityType.Client
            );

            await _state.WriteStateAsync();
        }

        public async Task DeactivateClient(string clientId, string reason)
        {
            if (_state.State.TryGetValue(clientId, out var existingClient))
            {
                _logger.LogInformation("Deactivating client: {ClientId}, Reason: {Reason}", clientId, reason);

                _state.State[clientId] = existingClient with
                {
                    Status = AgentStatus.Deactivated,
                    DeactivatedAt = DateTime.UtcNow,
                    DeactivationReason = reason
                };

                await _state.WriteStateAsync();
            }
            else
            {
                _logger.LogWarning("Attempted to deactivate unknown client: {ClientId}", clientId);
            }
        }

        public async Task DeactivateAgent(string key, string reason)
        {
            if (_state.State.TryGetValue(key, out var existingAgent))
            {
                _logger.LogInformation("Deactivating agent: {Key}, Reason: {Reason}", key, reason);

                // Update to deactivated status but KEEP in registry
                _state.State[key] = existingAgent with
                {
                    Status = AgentStatus.Deactivated,
                    DeactivatedAt = DateTime.UtcNow,
                    DeactivationReason = reason
                };

                await _state.WriteStateAsync();
            }
            else
            {
                _logger.LogWarning("Attempted to deactivate unknown agent: {Key}", key);
            }
        }

        public Task<List<AgentInfo>> GetAllAgents()
        {
            return Task.FromResult(_state.State.Values.ToList());
        }

        public Task<List<AgentInfo>> GetActiveAgents()
        {
            var active = _state.State.Values
                .Where(a => a.Status == AgentStatus.Active)
                .ToList();
            return Task.FromResult(active);
        }

        public Task<List<AgentInfo>> GetDeactivatedAgents()
        {
            var deactivated = _state.State.Values
                .Where(a => a.Status == AgentStatus.Deactivated)
                .ToList();
            return Task.FromResult(deactivated);
        }

        public Task<AgentInfo?> GetAgentInfo(string key)
        {
            _state.State.TryGetValue(key, out var info);
            return Task.FromResult(info);
        }

        public Task<List<AgentInfo>> GetAllByEntityType(EntityType entityType)
        {
            var filtered = _state.State.Values
                .Where(a => a.EntityType == entityType)
                .ToList();
            return Task.FromResult(filtered);
        }

        public Task<List<AgentInfo>> GetActiveByEntityType(EntityType entityType)
        {
            var filtered = _state.State.Values
                .Where(a => a.EntityType == entityType && a.Status == AgentStatus.Active)
                .ToList();
            return Task.FromResult(filtered);
        }

        public async Task<int> PurgeDeactivatedAgentsOlderThan(TimeSpan age)
        {
            var cutoffTime = DateTime.UtcNow - age;
            var toRemove = _state.State
                .Where(kvp => kvp.Value.Status == AgentStatus.Deactivated
                           && kvp.Value.DeactivatedAt.HasValue
                           && kvp.Value.DeactivatedAt.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _state.State.Remove(key);
            }

            if (toRemove.Count > 0)
            {
                await _state.WriteStateAsync();
                _logger.LogInformation("Purged {Count} deactivated agents older than {Age}",
                    toRemove.Count, age);
            }

            return toRemove.Count;
        }

        public Task<Dictionary<string, int>> GetAgentStatistics()
        {
            var stats = new Dictionary<string, int>
            {
                ["Total"] = _state.State.Count,
                ["Active"] = _state.State.Values.Count(a => a.Status == AgentStatus.Active),
                ["Deactivated"] = _state.State.Values.Count(a => a.Status == AgentStatus.Deactivated)
            };

            // Group by agent type
            var byType = _state.State.Values
                .GroupBy(a => a.AgentType)
                .ToDictionary(g => $"Type_{g.Key}", g => g.Count());

            foreach (var kvp in byType)
            {
                stats[kvp.Key] = kvp.Value;
            }

            return Task.FromResult(stats);
        }
    }
}
