using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Configuration;
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
            [PersistentState("agentRegistry", FabrCoreOrleansConstants.StorageProviderName)]
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

        public async Task RegisterPrincipal(string principalHandle)
        {
            _logger.LogInformation("Registering principal: {PrincipalHandle}", principalHandle);

            _state.State[principalHandle] = new AgentInfo(
                Key: principalHandle,
                AgentType: "Principal",
                Handle: principalHandle,
                Status: AgentStatus.Active,
                ActivatedAt: DateTime.UtcNow,
                DeactivatedAt: null,
                DeactivationReason: null,
                EntityType: EntityType.Principal
            );

            await _state.WriteStateAsync();
        }

        public async Task DeactivatePrincipal(string principalHandle, string reason)
        {
            if (_state.State.TryGetValue(principalHandle, out var existingPrincipal))
            {
                _logger.LogInformation("Deactivating principal: {PrincipalHandle}, Reason: {Reason}", principalHandle, reason);

                _state.State[principalHandle] = existingPrincipal with
                {
                    Status = AgentStatus.Deactivated,
                    DeactivatedAt = DateTime.UtcNow,
                    DeactivationReason = reason
                };

                await _state.WriteStateAsync();
            }
            else
            {
                _logger.LogWarning("Attempted to deactivate unknown principal: {PrincipalHandle}", principalHandle);
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

        public async Task<bool> RemoveAgent(string key)
        {
            if (!_state.State.Remove(key))
            {
                _logger.LogInformation("Agent not present in registry during removal: {Key}", key);
                return false;
            }

            await _state.WriteStateAsync();
            _logger.LogInformation("Removed agent from registry: {Key}", key);
            return true;
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
                           && kvp.Value.EntityType == EntityType.Agent
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
                ["Deactivated"] = _state.State.Values.Count(a => a.Status == AgentStatus.Deactivated),
                ["AgentTotal"] = _state.State.Values.Count(a => a.EntityType == EntityType.Agent),
                ["AgentActive"] = _state.State.Values.Count(a => a.EntityType == EntityType.Agent && a.Status == AgentStatus.Active),
                ["AgentDeactivated"] = _state.State.Values.Count(a => a.EntityType == EntityType.Agent && a.Status == AgentStatus.Deactivated),
                ["PrincipalTotal"] = _state.State.Values.Count(a => a.EntityType == EntityType.Principal),
                ["PrincipalActive"] = _state.State.Values.Count(a => a.EntityType == EntityType.Principal && a.Status == AgentStatus.Active),
                ["PrincipalDeactivated"] = _state.State.Values.Count(a => a.EntityType == EntityType.Principal && a.Status == AgentStatus.Deactivated)
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
