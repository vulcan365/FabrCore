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
        // ── Message Recording ──

        /// <summary>Records a message flowing through the system.</summary>
        Task RecordMessageAsync(MonitoredMessage message);

        // ── Message Queries ──

        /// <summary>
        /// Gets recorded messages, optionally filtered by agent handle.
        /// Returns most recent messages first.
        /// </summary>
        Task<List<MonitoredMessage>> GetMessagesAsync(string? agentHandle = null, int? limit = null);

        /// <summary>Gets accumulated LLM token usage for a specific agent.</summary>
        Task<AgentTokenSummary?> GetAgentTokenSummaryAsync(string agentHandle);

        /// <summary>Gets accumulated LLM token usage for all agents.</summary>
        Task<List<AgentTokenSummary>> GetAllAgentTokenSummariesAsync();

        // ── Event Recording ──

        /// <summary>
        /// Records an event that reached an agent's <c>OnEvent</c> handler via the event stream.
        /// Events are stored in a separate buffer from messages so consumers can filter
        /// to messages only, events only, or both.
        /// </summary>
        Task RecordEventAsync(MonitoredEvent evt);

        // ── Event Queries ──

        /// <summary>
        /// Gets recorded events, optionally filtered by agent handle.
        /// Returns most recent events first.
        /// </summary>
        Task<List<MonitoredEvent>> GetEventsAsync(string? agentHandle = null, int? limit = null);

        // ── Maintenance ──

        /// <summary>Clears all recorded messages, events, and token summaries.</summary>
        Task ClearAsync();

        // ── Notifications ──

        /// <summary>
        /// Raised when a new message is recorded. Subscribe to push updates to a UI or external system.
        /// Implementations must ensure subscriber exceptions do not propagate to the caller.
        /// </summary>
        event Action<MonitoredMessage>? OnMessageRecorded;

        /// <summary>
        /// Raised when a new event is recorded. Subscribe alongside or instead of
        /// <see cref="OnMessageRecorded"/> to filter a viewer to messages, events, or both.
        /// Implementations must ensure subscriber exceptions do not propagate to the caller.
        /// </summary>
        event Action<MonitoredEvent>? OnEventRecorded;

        // ── LLM Call Recording ──

        /// <summary>
        /// Records a single LLM request/response call made by an agent. Stored in a buffer
        /// independent from messages and events so consumers can toggle visibility per track.
        /// </summary>
        Task RecordLlmCallAsync(MonitoredLlmCall call);

        // ── LLM Call Queries ──

        /// <summary>
        /// Gets recorded LLM calls, optionally filtered by agent handle.
        /// Returns most recent calls first.
        /// </summary>
        Task<List<MonitoredLlmCall>> GetLlmCallsAsync(string? agentHandle = null, int? limit = null);

        // ── LLM Call Notifications ──

        /// <summary>
        /// Raised when a new LLM call is recorded. Subscribe to push internal LLM request/response
        /// traffic to a viewer. Implementations must ensure subscriber exceptions do not propagate.
        /// </summary>
        event Action<MonitoredLlmCall>? OnLlmCallRecorded;

        // ── LLM Capture Configuration ──

        /// <summary>
        /// Options controlling LLM call capture behavior. Read by the chat client wrapper on
        /// every call to cheaply decide whether to materialize payloads.
        /// </summary>
        LlmCaptureOptions LlmCaptureOptions { get; }
    }
}
