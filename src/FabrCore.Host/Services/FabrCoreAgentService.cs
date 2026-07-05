using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Streaming;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using Orleans;
using System.Diagnostics;
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
        private readonly IAgentManagementProvider _managementProvider;
        private readonly ILogger<FabrCoreAgentService> _logger;

        public FabrCoreAgentService(
            IClusterClient clusterClient,
            IFabrCoreRegistry registry,
            IAgentManagementProvider managementProvider,
            ILogger<FabrCoreAgentService> logger)
        {
            _clusterClient = clusterClient;
            _registry = registry;
            _managementProvider = managementProvider;
            _logger = logger;
        }

        private static string BuildAgentKey(string userHandle, string handle) => $"{userHandle}:{handle}";

        // ── Agent Communication ──

        public async Task<AgentHealthStatus> ConfigureAgentAsync(string userHandle, AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            var normalizedConfig = NormalizeServiceConfig(userHandle, config, nameof(config));
            var agentHandle = normalizedConfig.Handle!;

            var clientGrain = _clusterClient.GetGrain<IClientGrain>(userHandle);
            var health = await clientGrain.CreateAgent(normalizedConfig);

            if (detailLevel != HealthDetailLevel.Basic)
            {
                health = await GetHealthAsync(userHandle, agentHandle, detailLevel);
            }

            return health;
        }

        public Task<AgentHealthStatus> ConfigureSystemAgentAsync(AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            return ConfigureAgentAsync("system", config, detailLevel);
        }

        public async Task<List<AgentHealthStatus>> ConfigureAgentsAsync(string userHandle, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            var results = new List<AgentHealthStatus>();

            foreach (var config in configs)
            {
                try
                {
                    var health = await ConfigureAgentAsync(userHandle, config, detailLevel);
                    results.Add(health);
                }
                catch (Exception ex)
                {
                    var key = BuildFailureKey(userHandle, config);
                    _logger.LogError(ex, "Error creating agent for user {userHandle} with handle {Handle}", userHandle, key);

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

        public async Task<List<AgentHealthStatus>> EnsureAgentsAsync(string userHandle, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
                throw new ArgumentException("User handle is required.", nameof(userHandle));
            if (configs == null)
                throw new ArgumentNullException(nameof(configs));

            var normalizedConfigs = configs
                .Select((config, index) => NormalizeBlueprintConfig(userHandle, config, index))
                .ToList();
            var clientGrain = _clusterClient.GetGrain<IClientGrain>(userHandle);
            var results = new List<AgentHealthStatus>(normalizedConfigs.Count);

            foreach (var config in normalizedConfigs)
            {
                var agentHandle = config.Handle!;
                try
                {
                    var health = await clientGrain.CreateAgent(config);

                    if (detailLevel != HealthDetailLevel.Basic)
                    {
                        health = await GetHealthAsync(userHandle, agentHandle, detailLevel);
                    }

                    results.Add(health);
                }
                catch (Exception ex)
                {
                    var key = BuildAgentKey(userHandle, agentHandle);
                    _logger.LogError(ex, "Error ensuring blueprint agent for user {userHandle} with handle {Handle}", userHandle, key);

                    results.Add(new AgentHealthStatus
                    {
                        Handle = key,
                        State = HealthState.Unhealthy,
                        Timestamp = DateTime.UtcNow,
                        IsConfigured = false,
                        Message = $"Failed to ensure agent: {ex.Message}"
                    });
                }
            }

            return results;
        }

        private static AgentConfiguration NormalizeBlueprintConfig(string userHandle, AgentConfiguration? config, int index)
        {
            if (config == null)
                throw new ArgumentException($"agents[{index}] is null.", nameof(config));
            if (string.IsNullOrWhiteSpace(config.Handle))
                throw new ArgumentException($"agents[{index}].Handle is required.", nameof(config));

            var (handleUser, agentHandle) = HandleUtilities.ParseHandle(config.Handle);
            if (!string.IsNullOrEmpty(handleUser) && !string.Equals(handleUser, userHandle, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"agents[{index}].Handle targets user '{handleUser}', but x-user-handle is '{userHandle}'. Cross-user blueprint handles are not allowed.",
                    nameof(config));
            }

            return new AgentConfiguration
            {
                Handle = agentHandle,
                AgentType = config.AgentType,
                Models = config.Models,
                Streams = config.Streams,
                SystemPrompt = config.SystemPrompt,
                Description = config.Description,
                Args = config.Args,
                Plugins = config.Plugins,
                Tools = config.Tools,
                McpServers = config.McpServers,
                ForceReconfigure = false
            };
        }

        private static AgentConfiguration NormalizeServiceConfig(string userHandle, AgentConfiguration? config, string paramName)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
                throw new ArgumentException("User handle is required.", nameof(userHandle));
            if (config == null)
                throw new ArgumentNullException(paramName);
            if (string.IsNullOrWhiteSpace(config.Handle))
                throw new ArgumentException("Agent handle is required.", paramName);

            var (handleUser, agentHandle) = HandleUtilities.ParseHandle(config.Handle);
            if (string.IsNullOrWhiteSpace(agentHandle))
                throw new ArgumentException("Agent handle is required.", paramName);

            if (!string.IsNullOrEmpty(handleUser) && !string.Equals(handleUser, userHandle, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Agent handle targets user '{handleUser}', but userHandle is '{userHandle}'. Cross-user agent configuration is not allowed.",
                    paramName);
            }

            return CloneAgentConfiguration(config, agentHandle, config.ForceReconfigure);
        }

        private static AgentConfiguration CloneAgentConfiguration(
            AgentConfiguration config,
            string handle,
            bool forceReconfigure)
        {
            return new AgentConfiguration
            {
                Handle = handle,
                AgentType = config.AgentType,
                Models = config.Models,
                Streams = config.Streams?.ToList() ?? new List<EventStreamSubscription>(),
                SystemPrompt = config.SystemPrompt,
                Description = config.Description,
                Args = config.Args != null
                    ? new Dictionary<string, string>(config.Args, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal),
                Plugins = config.Plugins?.ToList() ?? new List<string>(),
                Tools = config.Tools?.ToList() ?? new List<string>(),
                McpServers = config.McpServers?.ToList() ?? new List<McpServerConfig>(),
                ForceReconfigure = forceReconfigure
            };
        }

        private static string BuildFailureKey(string? userHandle, AgentConfiguration? config)
        {
            var handle = config?.Handle;
            if (string.IsNullOrWhiteSpace(handle))
                return string.IsNullOrWhiteSpace(userHandle) ? "<unknown>" : BuildAgentKey(userHandle, "<unknown>");

            if (handle.Contains(':') || string.IsNullOrWhiteSpace(userHandle))
                return handle;

            return BuildAgentKey(userHandle, handle);
        }

        public async Task SendMessageAsync(string userHandle, string handle, string message)
        {
            var key = BuildAgentKey(userHandle, handle);
            var stream = _clusterClient.GetAgentChatStream(key);
            var msg = new AgentMessage
            {
                ToHandle = key,
                FromHandle = "AgentService",
                Message = message,
                Kind = MessageKind.OneWay
            };
            msg.StampFromActivity(Activity.Current);
            await stream.OnNextAsync(msg);
            _logger.LogDebug("Fire-and-forget message sent to agent {Handle} for user {userHandle}", handle, userHandle);
        }

        public async Task SendMessageAsync(string userHandle, string handle, AgentMessage message)
        {
            var key = BuildAgentKey(userHandle, handle);
            message.ToHandle = key;
            if (string.IsNullOrEmpty(message.TraceId))
                message.StampFromActivity(Activity.Current);
            var stream = _clusterClient.GetAgentChatStream(key);
            await stream.OnNextAsync(message);
            _logger.LogDebug("Fire-and-forget message sent to agent {Handle} for user {userHandle}", handle, userHandle);
        }

        public async Task<AgentMessage> SendAndReceiveMessageAsync(string userHandle, string handle, string message)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            var msg = new AgentMessage
            {
                ToHandle = key,
                FromHandle = "AgentService",
                Message = message
            };
            msg.StampFromActivity(Activity.Current);
            return await proxy.OnMessage(msg);
        }

        public async Task<AgentMessage> SendAndReceiveMessageAsync(string userHandle, string handle, AgentMessage message)
        {
            var key = BuildAgentKey(userHandle, handle);
            message.ToHandle = key;
            if (string.IsNullOrEmpty(message.TraceId))
                message.StampFromActivity(Activity.Current);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.OnMessage(message);
        }

        public async Task<AgentHealthStatus> GetHealthAsync(string userHandle, string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.GetHealth(detailLevel);
        }

        public async Task<AgentEvictionResult> EvictAgentAsync(string userHandle, string handle)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            var result = await proxy.EvictAgent();

            var clientTrackingRemoved = false;
            try
            {
                var clientGrain = _clusterClient.GetGrain<IClientGrain>(userHandle);
                clientTrackingRemoved = await clientGrain.UntrackAgent(handle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent evicted but failed to remove client tracking for {UserHandle}:{Handle}",
                    userHandle, handle);
                throw;
            }

            return result with
            {
                ClientTrackingRemoved = clientTrackingRemoved,
                Existed = result.Existed || clientTrackingRemoved
            };
        }

        public async Task SendEventAsync(string userHandle, string handle, EventMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.Namespace))
            {
                message.Channel = BuildAgentKey(userHandle, handle);
            }

            var streamName = EventStreamSubscription.ToStreamName(message);
            var stream = _clusterClient.GetStream<EventMessage>(streamName);
            await stream.OnNextAsync(message);
            _logger.LogDebug("Event published to stream {StreamName} for user {userHandle}", streamName, userHandle);
        }

        // ── Agent Management (registration / lifecycle) ──

        public Task RegisterAgentAsync(string key, string agentType, string handle)
            => _managementProvider.RegisterAgentAsync(key, agentType, handle);

        public Task DeactivateAgentAsync(string key, string reason)
            => _managementProvider.DeactivateAgentAsync(key, reason);

        public Task<bool> RemoveAgentAsync(string key)
            => _managementProvider.RemoveAgentAsync(key);

        public Task RegisterClientAsync(string clientId)
            => _managementProvider.RegisterClientAsync(clientId);

        public Task DeactivateClientAsync(string clientId, string reason)
            => _managementProvider.DeactivateClientAsync(clientId, reason);

        // ── Diagnostics ──

        public Task<List<AgentInfo>> GetAgentsAsync(string? status = null)
            => _managementProvider.GetByEntityTypeAsync(EntityType.Agent, ParseStatus(status));

        public async Task<AgentInfo?> GetAgentInfoAsync(string key)
        {
            var entry = await _managementProvider.GetByKeyAsync(key);
            return entry?.EntityType == EntityType.Agent ? entry : null;
        }

        public Task<List<AgentInfo>> GetUsersAsync(string? status = null)
            => _managementProvider.GetByEntityTypeAsync(EntityType.Client, ParseStatus(status));

        public async Task<AgentInfo?> GetUserInfoAsync(string handle)
        {
            var entry = await _managementProvider.GetByKeyAsync(handle);
            return entry?.EntityType == EntityType.Client ? entry : null;
        }

        public Task<Dictionary<string, int>> GetAgentStatisticsAsync()
            => _managementProvider.GetStatisticsAsync();

        public Task<int> PurgeDeactivatedAgentsAsync(TimeSpan olderThan)
            => _managementProvider.PurgeDeactivatedAsync(olderThan);

        public Task<List<AgentInfo>> GetAgentsByEntityTypeAsync(EntityType entityType)
            => _managementProvider.GetByEntityTypeAsync(entityType);

        private static AgentStatus? ParseStatus(string? status)
        {
            return status?.ToLowerInvariant() switch
            {
                "active" => AgentStatus.Active,
                "deactivated" => AgentStatus.Deactivated,
                _ => null
            };
        }

        // ── Discovery ──

        public List<RegistryEntry> GetAgentTypes() => _registry.GetAgentTypes();

        public List<RegistryEntry> GetPlugins() => _registry.GetPlugins();

        public List<RegistryEntry> GetTools() => _registry.GetTools();

        public List<RegistryCollision> GetCollisions() => _registry.GetCollisions();

        // ── Thread Management ──

        public async Task<List<StoredChatMessage>> GetThreadMessagesAsync(string userHandle, string handle, string threadId)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.GetThreadMessages(threadId);
        }

        public async Task AddThreadMessagesAsync(string userHandle, string handle, string threadId, IEnumerable<StoredChatMessage> messages)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.AddThreadMessages(threadId, messages);
        }

        public async Task ClearThreadMessagesAsync(string userHandle, string handle, string threadId)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.ClearThreadMessages(threadId);
        }

        public async Task ReplaceThreadMessagesAsync(string userHandle, string handle, string threadId, IEnumerable<StoredChatMessage> messages)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.ReplaceThreadMessages(threadId, messages);
        }

        // ── Custom State ──

        public async Task<Dictionary<string, JsonElement>> GetCustomStateAsync(string userHandle, string handle)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            return await proxy.GetCustomStateAsync();
        }

        public async Task MergeCustomStateAsync(string userHandle, string handle, Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
        {
            var key = BuildAgentKey(userHandle, handle);
            var proxy = _clusterClient.GetGrain<IAgentGrain>(key);
            await proxy.MergeCustomStateAsync(changes, deletes);
        }
    }
}
