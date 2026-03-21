using Orleans;

namespace FabrCore.Core
{
    /// <summary>
    /// A CloudEvents-inspired message structure for fire-and-forget event delivery.
    /// Events carry their own routing information (Namespace + Channel) and are
    /// delivered via Orleans streams without the request/response semantics of AgentMessage.
    /// </summary>
    public class EventMessage
    {
        /// <summary>Unique event identifier.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Event type descriptor (e.g. "order.created", "agent.status").</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Handle of the event producer.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Optional subject or topic for additional categorization.</summary>
        public string? Subject { get; set; }

        /// <summary>Timestamp when the event was created.</summary>
        public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Stream namespace for delivery routing.</summary>
        public string Namespace { get; set; } = string.Empty;

        /// <summary>Channel within the namespace for routing.</summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>String payload.</summary>
        public string? Data { get; set; }

        /// <summary>MIME type of the data payload (e.g. "application/json").</summary>
        public string? DataContentType { get; set; }

        /// <summary>Binary payload.</summary>
        public byte[]? BinaryData { get; set; }

        /// <summary>Optional key-value arguments.</summary>
        public Dictionary<string, string>? Args { get; set; }

        /// <summary>Trace/correlation identifier for distributed tracing.</summary>
        public string? TraceId { get; set; } = Guid.NewGuid().ToString();
    }

    [GenerateSerializer]
    internal struct EventMessageSurrogate
    {
        public EventMessageSurrogate() { }

        [Id(0)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Id(1)]
        public string Type { get; set; } = string.Empty;

        [Id(2)]
        public string Source { get; set; } = string.Empty;

        [Id(3)]
        public string? Subject { get; set; }

        [Id(4)]
        public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

        [Id(5)]
        public string Namespace { get; set; } = string.Empty;

        [Id(6)]
        public string Channel { get; set; } = string.Empty;

        [Id(7)]
        public string? Data { get; set; }

        [Id(8)]
        public string? DataContentType { get; set; }

        [Id(9)]
        public byte[]? BinaryData { get; set; }

        [Id(10)]
        public Dictionary<string, string>? Args { get; set; }

        [Id(11)]
        public string? TraceId { get; set; } = Guid.NewGuid().ToString();
    }

    [RegisterConverter]
    internal sealed class EventMessageSurrogateConverter : IConverter<EventMessage, EventMessageSurrogate>
    {
        public EventMessage ConvertFromSurrogate(in EventMessageSurrogate surrogate)
        {
            return new EventMessage
            {
                Id = surrogate.Id,
                Type = surrogate.Type,
                Source = surrogate.Source,
                Subject = surrogate.Subject,
                Time = surrogate.Time,
                Namespace = surrogate.Namespace,
                Channel = surrogate.Channel,
                Data = surrogate.Data,
                DataContentType = surrogate.DataContentType,
                BinaryData = surrogate.BinaryData,
                Args = surrogate.Args,
                TraceId = surrogate.TraceId
            };
        }

        public EventMessageSurrogate ConvertToSurrogate(in EventMessage value)
        {
            return new EventMessageSurrogate
            {
                Id = value.Id,
                Type = value.Type,
                Source = value.Source,
                Subject = value.Subject,
                Time = value.Time,
                Namespace = value.Namespace,
                Channel = value.Channel,
                Data = value.Data,
                DataContentType = value.DataContentType,
                BinaryData = value.BinaryData,
                Args = value.Args,
                TraceId = value.TraceId
            };
        }
    }
}
