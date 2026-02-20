using Fabr.Core;

namespace Fabr.Client
{
    /// <summary>
    /// Represents a client context for communicating with the Fabr agent cluster.
    /// Each context is bound to a specific handle (user/client identifier) and is immutable after creation.
    /// </summary>
    /// <remarks>
    /// Thread Safety: All operations are thread-safe after initialization.
    /// The handle is set once during creation and cannot be changed.
    /// Use IClientContextFactory to create instances.
    /// </remarks>
    public interface IClientContext : IAsyncDisposable
    {
        /// <summary>
        /// Gets the handle (user/client identifier) this context is bound to.
        /// </summary>
        string Handle { get; }

        /// <summary>
        /// Gets whether this context has been disposed.
        /// </summary>
        bool IsDisposed { get; }

        /// <summary>
        /// Event raised when an asynchronous message is received from an agent.
        /// </summary>
        /// <remarks>
        /// Event handlers are invoked on Orleans thread pool threads.
        /// Handlers should be short-lived or queue work for async processing.
        /// </remarks>
        event EventHandler<AgentMessage>? AgentMessageReceived;

        /// <summary>
        /// Sends a message and waits for a response (request-response pattern).
        /// </summary>
        /// <param name="request">The message to send.</param>
        /// <returns>The response message from the target agent.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the context has been disposed.</exception>
        Task<AgentMessage> SendAndReceiveMessage(AgentMessage request);

        /// <summary>
        /// Sends a message without waiting for a response (fire-and-forget pattern).
        /// </summary>
        /// <param name="request">The message to send.</param>
        /// <exception cref="ObjectDisposedException">Thrown if the context has been disposed.</exception>
        Task SendMessage(AgentMessage request);

        /// <summary>
        /// Sends an event to an agent's AgentEvent stream (fire-and-forget).
        /// Events are delivered to the agent's OnEvent handler, not OnMessage.
        /// If streamName is provided, publishes to the named event stream instead of the agent's default stream.
        /// </summary>
        /// <param name="request">The event message to send.</param>
        /// <param name="streamName">Optional named event stream to publish to.</param>
        /// <exception cref="ObjectDisposedException">Thrown if the context has been disposed.</exception>
        Task SendEvent(AgentMessage request, string? streamName = null);

        /// <summary>
        /// Creates a new agent with the specified configuration.
        /// </summary>
        /// <param name="agentConfiguration">The agent configuration.</param>
        /// <returns>Health status of the created agent.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the context has been disposed.</exception>
        Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration);

        /// <summary>
        /// Gets the health status of an agent.
        /// </summary>
        /// <param name="handle">The agent handle (without client prefix).</param>
        /// <param name="detailLevel">Level of health detail to return.</param>
        /// <returns>Health status of the agent.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the context has been disposed.</exception>
        Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);

        /// <summary>
        /// Gets the list of agents created by this client.
        /// </summary>
        /// <returns>List of tracked agents with handle and type.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the context has been disposed.</exception>
        Task<List<TrackedAgentInfo>> GetTrackedAgents();

        /// <summary>
        /// Checks if an agent with the specified handle is tracked (was previously created).
        /// </summary>
        /// <param name="handle">The agent handle (without client prefix).</param>
        /// <returns>True if the agent is tracked, false otherwise.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the context has been disposed.</exception>
        Task<bool> IsAgentTracked(string handle);
    }
}
