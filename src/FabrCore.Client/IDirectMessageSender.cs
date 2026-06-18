using FabrCore.Core;

namespace FabrCore.Client
{
    /// <summary>
    /// Provides direct message sending to agents without using ClientContext or ClientGrain infrastructure.
    /// All messages sent through this interface are automatically marked as OneWay since there is no
    /// mechanism to receive responses.
    /// </summary>
    public interface IDirectMessageSender
    {
        /// <summary>
        /// Sends a message directly to an agent's chat stream.
        /// The message will be automatically marked as OneWay (fire-and-forget).
        /// </summary>
        /// <param name="message">The message to send. ToHandle must be set to the target agent's handle.</param>
        Task SendMessageAsync(AgentMessage message);

        /// <summary>
        /// Sends an event directly to the stream identified by the event Namespace and Channel (fire-and-forget).
        /// Leave Namespace empty to send to the default AgentEvent stream for the target Channel.
        /// </summary>
        /// <param name="message">The event to send. For the default AgentEvent stream, Channel must be a fully-qualified target agent handle.</param>
        Task SendEventAsync(EventMessage message);
    }
}
