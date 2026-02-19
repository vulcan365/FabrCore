using Fabr.Core;
using System.Text.Json;

namespace Fabr.Sdk
{
    public interface IFabrAgentHost
    {
        string GetHandle();
        Task<AgentMessage> SendAndReceiveMessage(AgentMessage request);
        Task SendMessage(AgentMessage request);

        /// <summary>
        /// Gets the health status of an agent by handle.
        /// If handle is null, returns the health of this agent.
        /// </summary>
        /// <param name="handle">The handle of the agent to query, or null for self.</param>
        /// <param name="detailLevel">Level of detail for the health status (default: Detailed).</param>
        /// <returns>The agent's health status.</returns>
        Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed);

        /// <summary>
        /// Sends an event message to another agent on the AgentEvent stream.
        /// Events are fire-and-forget notifications that don't expect a response.
        /// If streamName is provided, publishes to the named event stream instead of the agent's default stream.
        /// </summary>
        /// <param name="request">The event message to send</param>
        /// <param name="streamName">Optional named event stream to publish to (bypasses handle normalization)</param>
        Task SendEvent(AgentMessage request, string? streamName = null);

        /// <summary>
        /// Registers a timer that will send a message to the agent at specified intervals.
        /// </summary>
        /// <param name="timerName">Unique name for the timer</param>
        /// <param name="messageType">The message type to send when timer fires</param>
        /// <param name="message">The message content to send when timer fires</param>
        /// <param name="dueTime">Time to wait before the first tick</param>
        /// <param name="period">Time between subsequent ticks (use TimeSpan.Zero for one-shot timer)</param>
        void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Unregisters a previously registered timer.
        /// </summary>
        /// <param name="timerName">The name of the timer to unregister</param>
        void UnregisterTimer(string timerName);

        /// <summary>
        /// Registers a persistent reminder that will send a message to the agent at specified intervals.
        /// Reminders survive grain deactivation and silo restarts.
        /// </summary>
        /// <param name="reminderName">Unique name for the reminder</param>
        /// <param name="messageType">The message type to send when reminder fires</param>
        /// <param name="message">The message content to send when reminder fires</param>
        /// <param name="dueTime">Time to wait before the first tick</param>
        /// <param name="period">Time between subsequent ticks (minimum 1 minute)</param>
        Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Unregisters a previously registered reminder.
        /// </summary>
        /// <param name="reminderName">The name of the reminder to unregister</param>
        Task UnregisterReminder(string reminderName);

        /// <summary>
        /// Gets or creates a persisted ChatHistoryProvider for the specified thread.
        /// Messages are buffered in memory until FlushAsync() is called.
        /// </summary>
        /// <param name="threadId">Unique identifier for the thread.</param>
        /// <returns>A FabrChatHistoryProvider backed by Orleans persistence.</returns>
        FabrChatHistoryProvider GetChatHistoryProvider(string threadId);

        /// <summary>
        /// Tracks a chat history provider for automatic flushing on grain deactivation and after message processing.
        /// Called automatically by FabrChatHistoryProvider.CreateFactory.
        /// </summary>
        /// <param name="provider">The chat history provider to track.</param>
        void TrackChatHistoryProvider(FabrChatHistoryProvider provider);

        /// <summary>
        /// Gets or creates a persisted ChatMessageStore for the specified thread.
        /// </summary>
        [Obsolete("Use GetChatHistoryProvider instead")]
        FabrChatHistoryProvider GetMessageStore(string threadId) => GetChatHistoryProvider(threadId);

        /// <summary>
        /// Tracks a message store for automatic flushing.
        /// </summary>
        [Obsolete("Use TrackChatHistoryProvider instead")]
        void TrackMessageStore(FabrChatHistoryProvider store) => TrackChatHistoryProvider(store);

        /// <summary>
        /// Gets messages for a thread from persistent storage.
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        /// <returns>List of stored chat messages for the thread.</returns>
        Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId);

        /// <summary>
        /// Adds messages to a thread in persistent storage.
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        /// <param name="messages">The messages to add.</param>
        Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages);

        /// <summary>
        /// Clears all messages for a thread.
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        Task ClearThreadAsync(string threadId);

        /// <summary>
        /// Atomically replaces all messages in a thread with a new set of messages.
        /// Used for compaction (summarizing old messages into fewer entries).
        /// </summary>
        /// <param name="threadId">The unique identifier for the thread.</param>
        /// <param name="messages">The replacement messages.</param>
        Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages);

        /// <summary>
        /// Gets the custom state dictionary from persistent storage.
        /// </summary>
        /// <returns>The custom state dictionary, or empty if none exists.</returns>
        Task<Dictionary<string, JsonElement>> GetCustomStateAsync();

        /// <summary>
        /// Merges custom state changes into persistent storage.
        /// </summary>
        /// <param name="changes">State entries to add or update.</param>
        /// <param name="deletes">Keys to remove from state.</param>
        Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes);
    }
}
