using Fabr.Core;

namespace Fabr.Client
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
        /// Sends an event directly to an agent's event stream.
        /// The message will be automatically marked as OneWay (fire-and-forget).
        /// If streamName is provided, publishes to the named event stream instead of the agent's default stream.
        /// </summary>
        /// <param name="message">The message to send. ToHandle must be set to the target agent's handle (unless using streamName).</param>
        /// <param name="streamName">Optional named event stream to publish to.</param>
        Task SendEventAsync(AgentMessage message, string? streamName = null);
    }
}
