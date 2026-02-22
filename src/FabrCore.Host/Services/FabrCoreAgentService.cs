using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Streaming;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using Orleans;
using System.Text.Json;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Unified service for agent communication, diagnostics, discovery,
    /// thread management, and custom state operations.
    /// </summary>
    public class FabrCoreAgentService : IFabrCoreAgentService
    {
        private readonly IClusterClient _clusterClient;
        private readonly IFabrCoreRegistry _registry;
        private readonly ILogger<FabrCoreAgentService> _logger;

        public FabrCoreAgentService(
            IClusterClient clusterClient,
            IFabrCoreRegistry registry,
            ILogger<FabrCoreAgentService> logger)
        {
            _clusterClient = clusterClient;
            _registry = registry;
            _logger = logger;
        }

        private static string BuildAgentKey(string userId, string handle) => $"{userId}:{handle}";

        // ── Agent Communication ──

        public async Task<AgentHealthStatus> ConfigureAgentAsync(string userId, AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            var key = BuildAgentKey(userId, config.Handle!);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.ConfigureAgent(config, detailLevel: detailLevel);
        }

        public async Task<List<AgentHealthStatus>> ConfigureAgentsAsync(string userId, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            var results = new List<AgentHealthStatus>();

            foreach (var config in configs)
            {
                try
                {
                    var health = await ConfigureAgentAsync(userId, config, detailLevel);
                    results.Add(health);
                }
                catch (Exception ex)
                {
                    var key = BuildAgentKey(userId, config.Handle!);
                    _logger.LogError(ex, "Error creating agent for user {UserId} with handle {Handle}", userId, key);

                    results.Add(new AgentHealthStatus
                    {
                        Handle = key,
                        State = HealthState.Unhealthy,
                        Timestamp = DateTime.UtcNow,
                        IsConfigured = false,
                        Message = $"Failed to create agent: {ex.Message}"
                    });
                }
            }

            return results;
        }

        public async Task SendMessageAsync(string userId, string handle, string message)
        {
            var key = BuildAgentKey(userId, handle);
            var stream = _clusterClient.GetAgentChatStream(key);
            await stream.OnNextAsync(new AgentMessage
            {
                ToHandle = key,
                FromHandle = "AgentService",
                Message = message,
                Kind = MessageKind.OneWay
            });
            _logger.LogDebug("Fire-and-forget message sent to agent {Handle} for user {UserId}", handle, userId);
        }

        public async Task SendMessageAsync(string userId, string handle, AgentMessage message)
        {
            var key = BuildAgentKey(userId, handle);
            var stream = _clusterClient.GetAgentChatStream(key);
            await stream.OnNextAsync(message);
            _logger.LogDebug("Fire-and-forget message sent to agent {Handle} for user {UserId}", handle, userId);
        }

        public async Task<AgentMessage> SendAndReceiveMessageAsync(string userId, string handle, string message)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.OnMessage(new AgentMessage
            {
                ToHandle = key,
                FromHandle = "AgentService",
                Message = message
            });
        }

        public async Task<AgentMessage> SendAndReceiveMessageAsync(string userId, string handle, AgentMessage message)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.OnMessage(message);
        }

        public async Task<AgentHealthStatus> GetHealthAsync(string userId, string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.GetHealth(detailLevel);
        }

        public async Task SendEventAsync(string userId, string handle, AgentMessage message, string? streamName = null)
        {
            if (streamName != null)
            {
                var stream = _clusterClient.GetAgentEventStream(streamName);
                await stream.OnNextAsync(message);
                _logger.LogDebug("Event published to named stream {StreamName} for user {UserId}", streamName, userId);
            }
            else
            {
                var key = BuildAgentKey(userId, handle);
                var stream = _clusterClient.GetAgentEventStream(key);
                await stream.OnNextAsync(message);
                _logger.LogDebug("Event published to agent {Handle} for user {UserId}", handle, userId);
            }
        }

        // ── Diagnostics ──

        public async Task<List<AgentInfo>> GetAgentsAsync(string? status = null)
        {
            var registry = _clusterClient.GetGrain<IAgentManagementGrain>(0);

            return status?.ToLower() switch
            {
                "active" => await registry.GetActiveAgents(),
                "deactivated" => await registry.GetDeactivatedAgents(),
                _ => await registry.GetAllAgents()
            };
        }

        public async Task<AgentInfo?> GetAgentInfoAsync(string key)
        {
            var registry = _clusterClient.GetGrain<IAgentManagementGrain>(0);
            return await registry.GetAgentInfo(key);
        }

        public async Task<Dictionary<string, int>> GetAgentStatisticsAsync()
        {
            var registry = _clusterClient.GetGrain<IAgentManagementGrain>(0);
            return await registry.GetAgentStatistics();
        }

        public async Task<int> PurgeDeactivatedAgentsAsync(TimeSpan olderThan)
        {
            var registry = _clusterClient.GetGrain<IAgentManagementGrain>(0);
            return await registry.PurgeDeactivatedAgentsOlderThan(olderThan);
        }

        public async Task<List<AgentInfo>> GetAgentsByEntityTypeAsync(EntityType entityType)
        {
            var registry = _clusterClient.GetGrain<IAgentManagementGrain>(0);
            return await registry.GetAllByEntityType(entityType);
        }

        // ── Discovery ──

        public List<RegistryEntry> GetAgentTypes() => _registry.GetAgentTypes();

        public List<RegistryEntry> GetPlugins() => _registry.GetPlugins();

        public List<RegistryEntry> GetTools() => _registry.GetTools();

        // ── Thread Management ──

        public async Task<List<StoredChatMessage>> GetThreadMessagesAsync(string userId, string handle, string threadId)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.GetThreadMessages(threadId);
        }

        public async Task AddThreadMessagesAsync(string userId, string handle, string threadId, IEnumerable<StoredChatMessage> messages)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.AddThreadMessages(threadId, messages);
        }

        public async Task ClearThreadMessagesAsync(string userId, string handle, string threadId)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.ClearThreadMessages(threadId);
        }

        public async Task ReplaceThreadMessagesAsync(string userId, string handle, string threadId, IEnumerable<StoredChatMessage> messages)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.ReplaceThreadMessages(threadId, messages);
        }

        // ── Custom State ──

        public async Task<Dictionary<string, JsonElement>> GetCustomStateAsync(string userId, string handle)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.GetCustomStateAsync();
        }

        public async Task MergeCustomStateAsync(string userId, string handle, Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
        {
            var key = BuildAgentKey(userId, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.MergeCustomStateAsync(changes, deletes);
        }
    }
}
