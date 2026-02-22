using FabrCore.Core;
using FabrCore.Sdk;
using System.Text.Json;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Unified service for agent communication, diagnostics, discovery,
    /// thread management, and custom state operations.
    /// </summary>
    public interface IFabrCoreAgentService
    {
        // ── Agent Communication ──

        /// <summary>
        /// Configures a single agent and returns its health status.
        /// </summary>
        Task<AgentHealthStatus> ConfigureAgentAsync(string userId, AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Configures multiple agents in batch. Failed configs get an Unhealthy entry.
        /// </summary>
        Task<List<AgentHealthStatus>> ConfigureAgentsAsync(string userId, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Sends a fire-and-forget message to an agent via the chat stream.
        /// The agent's OnMessage handler is invoked asynchronously.
        /// </summary>
        Task SendMessageAsync(string userId, string handle, string message);

        /// <summary>
        /// Sends a fire-and-forget AgentMessage to an agent via the chat stream.
        /// The agent's OnMessage handler is invoked asynchronously.
        /// </summary>
        Task SendMessageAsync(string userId, string handle, AgentMessage message);

        /// <summary>
        /// Sends a message to an agent via direct grain RPC and waits for the response.
        /// </summary>
        Task<AgentMessage> SendAndReceiveMessageAsync(string userId, string handle, string message);

        /// <summary>
        /// Sends an AgentMessage to an agent via direct grain RPC and waits for the response.
        /// </summary>
        Task<AgentMessage> SendAndReceiveMessageAsync(string userId, string handle, AgentMessage message);

        /// <summary>
        /// Gets the health status of an agent.
        /// </summary>
        Task<AgentHealthStatus> GetHealthAsync(string userId, string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Sends a fire-and-forget event via the agent's event stream.
        /// When streamName is provided, publishes to that named stream instead.
        /// </summary>
        Task SendEventAsync(string userId, string handle, AgentMessage message, string? streamName = null);

        // ── Diagnostics ──

        /// <summary>
        /// Gets all agents, optionally filtered by status ("active" or "deactivated").
        /// </summary>
        Task<List<AgentInfo>> GetAgentsAsync(string? status = null);

        /// <summary>
        /// Gets info for a specific agent by its full key.
        /// </summary>
        Task<AgentInfo?> GetAgentInfoAsync(string key);

        /// <summary>
        /// Gets agent statistics (total, active, deactivated counts).
        /// </summary>
        Task<Dictionary<string, int>> GetAgentStatisticsAsync();

        /// <summary>
        /// Purges deactivated agents older than the specified timespan.
        /// Returns the number of purged agents.
        /// </summary>
        Task<int> PurgeDeactivatedAgentsAsync(TimeSpan olderThan);

        /// <summary>
        /// Gets agents filtered by entity type.
        /// </summary>
        Task<List<AgentInfo>> GetAgentsByEntityTypeAsync(EntityType entityType);

        // ── Discovery ──

        /// <summary>
        /// Gets all registered agent types.
        /// </summary>
        List<RegistryEntry> GetAgentTypes();

        /// <summary>
        /// Gets all registered plugins.
        /// </summary>
        List<RegistryEntry> GetPlugins();

        /// <summary>
        /// Gets all registered tools.
        /// </summary>
        List<RegistryEntry> GetTools();

        // ── Thread Management ──

        /// <summary>
        /// Gets all messages for a specific thread from an agent's persistent storage.
        /// </summary>
        Task<List<StoredChatMessage>> GetThreadMessagesAsync(string userId, string handle, string threadId);

        /// <summary>
        /// Adds messages to a specific thread in an agent's persistent storage.
        /// </summary>
        Task AddThreadMessagesAsync(string userId, string handle, string threadId, IEnumerable<StoredChatMessage> messages);

        /// <summary>
        /// Clears all messages for a specific thread.
        /// </summary>
        Task ClearThreadMessagesAsync(string userId, string handle, string threadId);

        /// <summary>
        /// Atomically replaces all messages in a specific thread with a new set.
        /// </summary>
        Task ReplaceThreadMessagesAsync(string userId, string handle, string threadId, IEnumerable<StoredChatMessage> messages);

        // ── Custom State ──

        /// <summary>
        /// Gets the custom state dictionary for an agent.
        /// </summary>
        Task<Dictionary<string, JsonElement>> GetCustomStateAsync(string userId, string handle);

        /// <summary>
        /// Merges custom state changes into an agent's persisted state.
        /// </summary>
        Task MergeCustomStateAsync(string userId, string handle, Dictionary<string, JsonElement> changes, IEnumerable<string> deletes);
    }
}
