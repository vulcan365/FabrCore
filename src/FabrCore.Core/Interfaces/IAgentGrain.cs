using Orleans;
using Orleans.Concurrency;
using System.Text.Json;

namespace FabrCore.Core.Interfaces
{
    internal interface IAgentGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Configures the agent with the specified configuration.
        /// If already configured, returns current health status unless forceReconfigure is true.
        /// This method is marked as interleaving to allow configuration
        /// while message processing is active.
        /// </summary>
        /// <param name="config">The agent configuration.</param>
        /// <param name="forceReconfigure">If true, reconfigures even if already configured (default: false).</param>
        /// <param name="detailLevel">Level of detail for the returned health status (default: Basic).</param>
        /// <returns>Health status after configuration.</returns>
        [AlwaysInterleave]
        Task<AgentHealthStatus> ConfigureAgent(
            AgentConfiguration config,
            bool forceReconfigure = false,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        Task<AgentMessage> OnMessage(AgentMessage request);

        /// <summary>
        /// Gets the current health status of the agent.
        /// This method is marked as interleaving to allow health checks
        /// while message processing is active.
        /// </summary>
        /// <param name="detailLevel">Level of detail for the health status (default: Basic).</param>
        /// <returns>Current health status.</returns>
        [AlwaysInterleave]
        Task<AgentHealthStatus> GetHealth(HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Gets all messages for a specific thread from persistent storage.
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        /// <returns>List of stored chat messages for the thread.</returns>
        Task<List<StoredChatMessage>> GetThreadMessages(string threadId);

        /// <summary>
        /// Adds messages to a specific thread in persistent storage.
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        /// <param name="messages">The messages to add.</param>
        Task AddThreadMessages(string threadId, IEnumerable<StoredChatMessage> messages);

        /// <summary>
        /// Clears all messages for a specific thread.
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        Task ClearThreadMessages(string threadId);

        /// <summary>
        /// Atomically replaces all messages in a specific thread with a new set of messages.
        /// Used for compaction (summarizing old messages into fewer entries).
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        /// <param name="messages">The replacement messages.</param>
        Task ReplaceThreadMessages(string threadId, IEnumerable<StoredChatMessage> messages);

        /// <summary>
        /// Gets the custom state dictionary.
        /// </summary>
        /// <returns>The custom state dictionary, or empty if none exists.</returns>
        Task<Dictionary<string, JsonElement>> GetCustomStateAsync();

        /// <summary>
        /// Merges custom state changes into the persisted state.
        /// </summary>
        /// <param name="changes">State entries to add or update.</param>
        /// <param name="deletes">Keys to remove from state.</param>
        Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes);
    }
}
