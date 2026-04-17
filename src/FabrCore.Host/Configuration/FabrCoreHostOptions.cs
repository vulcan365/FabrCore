namespace FabrCore.Host.Configuration
{
    /// <summary>
    /// Runtime-tunable host settings bound from configuration section <c>FabrCore:Host</c>.
    /// </summary>
    public class FabrCoreHostOptions
    {
        public const string SectionName = "FabrCore:Host";

        /// <summary>
        /// WebSocket endpoint path. Defaults to <c>/ws</c>. Must start with "/".
        /// </summary>
        public string WebSocketPath { get; set; } = "/ws";

        /// <summary>
        /// Maximum size (in bytes) of a single inbound WebSocket message. Messages
        /// exceeding this size cause the session to close with
        /// <c>WebSocketCloseStatus.MessageTooBig</c>. Default 1 MB.
        /// </summary>
        public int MaxIncomingMessageBytes { get; set; } = 1 * 1024 * 1024;

        /// <summary>
        /// Capacity of the outbound per-session send queue. At capacity the oldest
        /// queued message is dropped (DropOldest). Default 256.
        /// </summary>
        public int OutboundQueueCapacity { get; set; } = 256;

        /// <summary>
        /// WebSocket keep-alive (ping) interval. Default 2 minutes.
        /// </summary>
        public TimeSpan WebSocketKeepAliveInterval { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Allowed <c>Origin</c> header values for browser-origin WebSocket upgrades.
        /// Empty list disables the check. Intended as defense-in-depth alongside
        /// <see cref="IWebSocketAuthenticator"/>.
        /// </summary>
        public List<string> AllowedWebSocketOrigins { get; set; } = new();
    }
}
