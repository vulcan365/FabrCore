namespace FabrCore.Core.Streaming
{
    /// <summary>
    /// Represents a fully-qualified Orleans stream name with provider, namespace, and handle.
    /// </summary>
    public readonly struct StreamName : IEquatable<StreamName>
    {
        /// <summary>
        /// The stream provider name.
        /// </summary>
        public string Provider { get; }

        /// <summary>
        /// The stream namespace (e.g., AgentChat, AgentEvent).
        /// </summary>
        public string Namespace { get; }

        /// <summary>
        /// The stream handle (agent or client identifier).
        /// </summary>
        public string Handle { get; }

        /// <summary>
        /// Creates a new StreamName with the specified components.
        /// </summary>
        public StreamName(string provider, string @namespace, string handle)
        {
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        }

        /// <summary>
        /// Creates an AgentChat stream name for the given handle.
        /// </summary>
        public static StreamName ForAgentChat(string handle)
            => new(StreamConstants.ProviderName, StreamConstants.AgentChatNamespace, handle);

        /// <summary>
        /// Creates an AgentEvent stream name for the given handle.
        /// </summary>
        public static StreamName ForAgentEvent(string handle)
            => new(StreamConstants.ProviderName, StreamConstants.AgentEventNamespace, handle);

        /// <summary>
        /// Parses a fully-qualified stream name string (e.g., "StreamProvider.AgentChat.myhandle").
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when streamName is null or empty.</exception>
        /// <exception cref="FormatException">Thrown when the format is invalid.</exception>
        public static StreamName Parse(string streamName)
        {
            if (string.IsNullOrWhiteSpace(streamName))
                throw new ArgumentException("Stream name cannot be null or empty", nameof(streamName));

            var parts = streamName.Split(StreamConstants.Delimiter);
            if (parts.Length != 3)
                throw new FormatException(
                    $"Invalid stream name format '{streamName}'. Expected format: Provider{StreamConstants.Delimiter}Namespace{StreamConstants.Delimiter}Handle");

            return new StreamName(parts[0], parts[1], parts[2]);
        }

        /// <summary>
        /// Tries to parse a stream name string, returning false if invalid.
        /// </summary>
        public static bool TryParse(string? streamName, out StreamName result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(streamName))
                return false;

            var parts = streamName.Split(StreamConstants.Delimiter);
            if (parts.Length != 3)
                return false;

            result = new StreamName(parts[0], parts[1], parts[2]);
            return true;
        }

        /// <summary>
        /// Returns true if this stream is in the AgentChat namespace.
        /// </summary>
        public bool IsAgentChat => Namespace == StreamConstants.AgentChatNamespace;

        /// <summary>
        /// Returns true if this stream is in the AgentEvent namespace.
        /// </summary>
        public bool IsAgentEvent => Namespace == StreamConstants.AgentEventNamespace;

        /// <summary>
        /// Returns the fully-qualified stream name string.
        /// </summary>
        public override string ToString()
            => $"{Provider}{StreamConstants.Delimiter}{Namespace}{StreamConstants.Delimiter}{Handle}";

        public bool Equals(StreamName other)
            => Provider == other.Provider && Namespace == other.Namespace && Handle == other.Handle;

        public override bool Equals(object? obj)
            => obj is StreamName other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(Provider, Namespace, Handle);

        public static bool operator ==(StreamName left, StreamName right) => left.Equals(right);
        public static bool operator !=(StreamName left, StreamName right) => !left.Equals(right);
    }
}
