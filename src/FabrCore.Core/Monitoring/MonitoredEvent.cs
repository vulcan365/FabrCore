namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// A captured snapshot of an <see cref="EventMessage"/> that reached an agent's
    /// <c>OnEvent</c> handler via the Orleans event stream. Events are fire-and-forget,
    /// so captures are always <see cref="MessageDirection.Inbound"/>.
    /// The <c>Data</c> and <c>BinaryData</c> payloads are intentionally excluded to keep
    /// the monitor buffer readable, matching the policy used for <see cref="MonitoredMessage"/>.
    /// </summary>
    public class MonitoredEvent
    {
        /// <summary>Unique monitor record id (distinct from the originating <see cref="EventId"/>).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>The originating <see cref="EventMessage.Id"/>.</summary>
        public string? EventId { get; set; }

        /// <summary>The agent handle that received this event.</summary>
        public string? AgentHandle { get; set; }

        /// <summary>Event type descriptor (e.g. "order.created", "agent.status").</summary>
        public string? Type { get; set; }

        /// <summary>Handle of the event producer.</summary>
        public string? Source { get; set; }

        /// <summary>Optional subject or topic for additional categorization.</summary>
        public string? Subject { get; set; }

        /// <summary>Stream namespace used for delivery routing.</summary>
        public string? Namespace { get; set; }

        /// <summary>Channel within the namespace used for routing.</summary>
        public string? Channel { get; set; }

        /// <summary>MIME type of the excluded data payload (e.g. "application/json").</summary>
        public string? DataContentType { get; set; }

        /// <summary>Optional key-value arguments carried by the event.</summary>
        public Dictionary<string, string>? Args { get; set; }

        /// <summary>Producer-stamped event time (<see cref="EventMessage.Time"/>).</summary>
        public DateTimeOffset EventTime { get; set; }

        /// <summary>UTC timestamp of when the monitor recorded this entry.</summary>
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Trace/correlation identifier for distributed tracing.</summary>
        public string? TraceId { get; set; }

        /// <summary>Direction relative to the recording agent. Always <see cref="MessageDirection.Inbound"/> today.</summary>
        public MessageDirection Direction { get; set; } = MessageDirection.Inbound;
    }
}
