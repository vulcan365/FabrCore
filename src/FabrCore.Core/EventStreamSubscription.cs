using FabrCore.Core.Streaming;
using Orleans;

namespace FabrCore.Core
{
    /// <summary>
    /// Typed helper for configuring an agent subscription to an event stream.
    /// The namespace and channel match the corresponding <see cref="EventMessage"/> routing properties.
    /// </summary>
    [GenerateSerializer]
    public sealed record EventStreamSubscription
    {
        /// <summary>
        /// The Orleans stream provider name.
        /// </summary>
        [Id(0)]
        public string Provider { get; init; } = StreamConstants.ProviderName;

        /// <summary>
        /// Event namespace. Matches <see cref="EventMessage.Namespace"/>.
        /// </summary>
        [Id(1)]
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Event channel within the namespace. Matches <see cref="EventMessage.Channel"/>.
        /// </summary>
        [Id(2)]
        public string Channel { get; init; } = string.Empty;

        public EventStreamSubscription()
        {
        }

        public EventStreamSubscription(string @namespace, string channel)
            : this(StreamConstants.ProviderName, @namespace, channel)
        {
        }

        public EventStreamSubscription(string provider, string @namespace, string channel)
        {
            Provider = provider;
            Namespace = @namespace;
            Channel = channel;
            Validate();
        }

        /// <summary>
        /// Creates a subscription for events published with the same namespace and channel.
        /// </summary>
        public static EventStreamSubscription For(string @namespace, string channel)
            => new(@namespace, channel);

        /// <summary>
        /// Creates a subscription for events published with the same namespace and channel.
        /// </summary>
        public static EventStreamSubscription From(EventMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            return For(message.Namespace, message.Channel);
        }

        /// <summary>
        /// Resolves the stream for publishing an event. Empty event namespaces use the default AgentEvent stream.
        /// </summary>
        public static StreamName ToStreamName(EventMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var @namespace = string.IsNullOrWhiteSpace(message.Namespace)
                ? StreamConstants.AgentEventNamespace
                : message.Namespace;

            return new EventStreamSubscription(StreamConstants.ProviderName, @namespace, message.Channel).ToStreamName();
        }

        /// <summary>
        /// Converts the subscription into the Orleans stream name used by the host.
        /// </summary>
        public StreamName ToStreamName()
        {
            Validate();
            return new StreamName(Provider, Namespace, Channel);
        }

        public override string ToString()
            => $"{Namespace}{StreamConstants.Delimiter}{Channel}";

        public void Validate()
        {
            ValidatePart(Provider, nameof(Provider));
            ValidatePart(Namespace, nameof(Namespace));
            ValidatePart(Channel, nameof(Channel));

            if (string.Equals(Namespace, StreamConstants.AgentChatNamespace, StringComparison.Ordinal))
                throw new ArgumentException("Event stream subscriptions cannot use the reserved AgentChat namespace.", nameof(Namespace));
        }

        private static void ValidatePart(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Event stream subscription {paramName} cannot be null or empty.", paramName);

            if (value.Contains(StreamConstants.Delimiter, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Event stream subscription {paramName} cannot contain dots because dots split provider, namespace, and channel.",
                    paramName);
            }
        }
    }
}
