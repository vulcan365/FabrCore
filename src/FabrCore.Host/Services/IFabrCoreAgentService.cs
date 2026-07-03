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
        /// Configures a single agent for the specified user and returns its health status.
        /// Agents are created through the user's client grain so they are tracked for that user.
        /// </summary>
        Task<AgentHealthStatus> ConfigureAgentAsync(string userHandle, AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Configures a system user agent (user handle = "system"). Use this for shared agents
        /// that multiple users can access via ACL rules.
        /// The agent grain key will be <c>"system:{config.Handle}"</c>.
        /// The system client grain tracks the created agent under the <c>"system"</c> user handle.
        /// </summary>
        Task<AgentHealthStatus> ConfigureSystemAgentAsync(AgentConfiguration config, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Configures multiple agents in batch. Failed configs get an Unhealthy entry.
        /// Successful agents are created through the user's client grain so they are tracked for that user.
        /// </summary>
        Task<List<AgentHealthStatus>> ConfigureAgentsAsync(string userHandle, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Ensures multiple agents exist for a user without reconfiguring already configured agents.
        /// Agents are created through the user's client grain so they are tracked for that user.
        /// </summary>
        Task<List<AgentHealthStatus>> EnsureAgentsAsync(string userHandle, List<AgentConfiguration> configs, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Sends a fire-and-forget message to an agent via the chat stream.
        /// The agent's OnMessage handler is invoked asynchronously.
        /// </summary>
        Task SendMessageAsync(string userHandle, string handle, string message);

        /// <summary>
        /// Sends a fire-and-forget AgentMessage to an agent via the chat stream.
        /// The agent's OnMessage handler is invoked asynchronously.
        /// </summary>
        Task SendMessageAsync(string userHandle, string handle, AgentMessage message);

        /// <summary>
        /// Sends a message to an agent via direct grain RPC and waits for the response.
        /// </summary>
        Task<AgentMessage> SendAndReceiveMessageAsync(string userHandle, string handle, string message);

        /// <summary>
        /// Sends an AgentMessage to an agent via direct grain RPC and waits for the response.
        /// </summary>
        Task<AgentMessage> SendAndReceiveMessageAsync(string userHandle, string handle, AgentMessage message);

        /// <summary>
        /// Gets the health status of an agent.
        /// </summary>
        Task<AgentHealthStatus> GetHealthAsync(string userHandle, string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Permanently evicts an agent and removes its persisted/runtime traces.
        /// </summary>
        Task<AgentEvictionResult> EvictAgentAsync(string userHandle, string handle);

        /// <summary>
        /// Sends a fire-and-forget event via the event stream identified by the event Namespace and Channel.
        /// </summary>
        Task SendEventAsync(string userHandle, string handle, EventMessage message);

        // ── Agent Management (registration / lifecycle) ──

        /// <summary>Registers an agent as active in the management provider.</summary>
        Task RegisterAgentAsync(string key, string agentType, string handle);

        /// <summary>Marks an agent as deactivated in the management provider.</summary>
        Task DeactivateAgentAsync(string key, string reason);

        /// <summary>Removes an agent completely from the management provider.</summary>
        Task<bool> RemoveAgentAsync(string key);

        /// <summary>Registers a client as active in the management provider.</summary>
        Task RegisterClientAsync(string clientId);

        /// <summary>Marks a client as deactivated in the management provider.</summary>
        Task DeactivateClientAsync(string clientId, string reason);

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

        /// <summary>
        /// Gets any alias collisions detected during registry scanning.
        /// </summary>
        List<RegistryCollision> GetCollisions();

        // ── Thread Management ──

        /// <summary>
        /// Gets all messages for a specific thread from an agent's persistent storage.
        /// </summary>
        Task<List<StoredChatMessage>> GetThreadMessagesAsync(string userHandle, string handle, string threadId);

        /// <summary>
        /// Adds messages to a specific thread in an agent's persistent storage.
        /// </summary>
        Task AddThreadMessagesAsync(string userHandle, string handle, string threadId, IEnumerable<StoredChatMessage> messages);

        /// <summary>
        /// Clears all messages for a specific thread.
        /// </summary>
        Task ClearThreadMessagesAsync(string userHandle, string handle, string threadId);

        /// <summary>
        /// Atomically replaces all messages in a specific thread with a new set.
        /// </summary>
        Task ReplaceThreadMessagesAsync(string userHandle, string handle, string threadId, IEnumerable<StoredChatMessage> messages);

        // ── Custom State ──

        /// <summary>
        /// Gets the custom state dictionary for an agent.
        /// </summary>
        Task<Dictionary<string, JsonElement>> GetCustomStateAsync(string userHandle, string handle);

        /// <summary>
        /// Merges custom state changes into an agent's persisted state.
        /// </summary>
        Task MergeCustomStateAsync(string userHandle, string handle, Dictionary<string, JsonElement> changes, IEnumerable<string> deletes);
    }
}
