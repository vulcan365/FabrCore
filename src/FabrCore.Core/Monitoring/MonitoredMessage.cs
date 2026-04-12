namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// A captured snapshot of a message flowing through the FabrCore agent system.
    /// </summary>
    public class MonitoredMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>The agent or client handle that recorded this entry.</summary>
        public string? AgentHandle { get; set; }

        public string? FromHandle { get; set; }
        public string? ToHandle { get; set; }
        public string? OnBehalfOfHandle { get; set; }
        public string? DeliverToHandle { get; set; }
        public string? Channel { get; set; }
        public string? Message { get; set; }
        public string? MessageType { get; set; }
        public MessageKind Kind { get; set; }
        public string? DataType { get; set; }
        public List<string> Files { get; set; } = new List<string>();
        public Dictionary<string, string>? State { get; set; }
        public Dictionary<string, string>? Args { get; set; }
        public MessageDirection Direction { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public string? TraceId { get; set; }

        /// <summary>LLM usage metrics. Populated on outbound responses that invoked an LLM.</summary>
        public LlmUsageInfo? LlmUsage { get; set; }

        /// <summary>True if this message was routed through the busy handler because the agent was already processing.</summary>
        public bool BusyRouted { get; set; }
    }
}
