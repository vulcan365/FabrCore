using FabrCore.Core;

namespace FabrCore.Client
{
    /// <summary>
    /// Interface for component-hosted agent proxies.
    /// </summary>
    public interface IFabrCoreClientAgentProxy : IAsyncDisposable
    {
        /// <summary>
        /// Gets the agent's handle (unique identifier).
        /// </summary>
        string Handle { get; }

        /// <summary>
        /// Gets whether the agent has been initialized.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Initializes the agent. Must be called before sending/receiving messages.
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Called when initialization completes. Override to perform setup.
        /// </summary>
        Task OnInitializeAsync();

        /// <summary>
        /// Handles incoming messages from other agents.
        /// </summary>
        Task<AgentMessage> OnMessageAsync(AgentMessage message);

        /// <summary>
        /// Handles incoming event notifications.
        /// </summary>
        Task OnEventAsync(AgentMessage message);
    }
}
