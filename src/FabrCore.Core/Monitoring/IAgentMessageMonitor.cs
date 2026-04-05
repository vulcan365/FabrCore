namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// Pluggable provider for monitoring agent message traffic and LLM usage.
    /// The default implementation (<c>InMemoryAgentMessageMonitor</c>) stores messages in a
    /// bounded in-memory buffer with FIFO eviction. Swap with a custom implementation
    /// (database, event hub, etc.) via <c>FabrCoreServerOptions.UseAgentMessageMonitor&lt;T&gt;()</c>.
    /// </summary>
    public interface IAgentMessageMonitor
    {
        // ── Recording ──

        /// <summary>Records a message flowing through the system.</summary>
        Task RecordMessageAsync(MonitoredMessage message);

        // ── Queries ──

        /// <summary>
        /// Gets recorded messages, optionally filtered by agent handle.
        /// Returns most recent messages first.
        /// </summary>
        Task<List<MonitoredMessage>> GetMessagesAsync(string? agentHandle = null, int? limit = null);

        /// <summary>Gets accumulated LLM token usage for a specific agent.</summary>
        Task<AgentTokenSummary?> GetAgentTokenSummaryAsync(string agentHandle);

        /// <summary>Gets accumulated LLM token usage for all agents.</summary>
        Task<List<AgentTokenSummary>> GetAllAgentTokenSummariesAsync();

        // ── Maintenance ──

        /// <summary>Clears all recorded messages and token summaries.</summary>
        Task ClearAsync();

        // ── Notifications ──

        /// <summary>
        /// Raised when a new message is recorded. Subscribe to push updates to a UI or external system.
        /// Implementations must ensure subscriber exceptions do not propagate to the caller.
        /// </summary>
        event Action<MonitoredMessage>? OnMessageRecorded;
    }
}
