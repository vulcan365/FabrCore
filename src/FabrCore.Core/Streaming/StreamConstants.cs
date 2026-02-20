namespace FabrCore.Core.Streaming
{
    /// <summary>
    /// Constants for Orleans streaming configuration.
    /// </summary>
    public static class StreamConstants
    {
        /// <summary>
        /// The name of the Orleans stream provider.
        /// </summary>
        public const string ProviderName = "StreamProvider";

        /// <summary>
        /// Stream namespace for agent-to-agent chat messages.
        /// </summary>
        public const string AgentChatNamespace = "AgentChat";

        /// <summary>
        /// Stream namespace for agent events (non-chat notifications).
        /// </summary>
        public const string AgentEventNamespace = "AgentEvent";

        /// <summary>
        /// Delimiter used in fully-qualified stream names.
        /// </summary>
        public const char Delimiter = '.';
    }
}
