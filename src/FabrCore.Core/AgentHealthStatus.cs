using Orleans;

namespace Fabr.Core
{
    /// <summary>
    /// Combined health status from AgentGrain and its FabrAgentProxy.
    /// </summary>
    [GenerateSerializer]
    public record AgentHealthStatus
    {
        // Basic level (always included)

        /// <summary>
        /// The agent's handle/key.
        /// </summary>
        [Id(0)]
        public required string Handle { get; init; }

        /// <summary>
        /// Overall health state of the agent.
        /// </summary>
        [Id(1)]
        public required HealthState State { get; init; }

        /// <summary>
        /// Timestamp when health was collected (UTC).
        /// </summary>
        [Id(2)]
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// Whether the agent has been configured.
        /// </summary>
        [Id(3)]
        public required bool IsConfigured { get; init; }

        /// <summary>
        /// Human-readable message about the agent health.
        /// </summary>
        [Id(4)]
        public string? Message { get; init; }

        // Detailed level

        /// <summary>
        /// The agent type alias.
        /// Only populated when detail level is Detailed or Full.
        /// </summary>
        [Id(5)]
        public string? AgentType { get; init; }

        /// <summary>
        /// How long the agent has been running.
        /// Only populated when detail level is Detailed or Full.
        /// </summary>
        [Id(6)]
        public TimeSpan? Uptime { get; init; }

        /// <summary>
        /// Total messages processed since configuration.
        /// Only populated when detail level is Detailed or Full.
        /// </summary>
        [Id(7)]
        public long? MessagesProcessed { get; init; }

        /// <summary>
        /// Number of active timers.
        /// Only populated when detail level is Detailed or Full.
        /// </summary>
        [Id(8)]
        public int? ActiveTimerCount { get; init; }

        /// <summary>
        /// Number of active reminders.
        /// Only populated when detail level is Detailed or Full.
        /// </summary>
        [Id(9)]
        public int? ActiveReminderCount { get; init; }

        /// <summary>
        /// Number of stream subscriptions.
        /// Only populated when detail level is Detailed or Full.
        /// </summary>
        [Id(10)]
        public int? StreamCount { get; init; }

        /// <summary>
        /// The full agent configuration including plugins, tools, system prompt, etc.
        /// Only populated when detail level is Detailed or Full.
        /// </summary>
        [Id(14)]
        public AgentConfiguration? Configuration { get; init; }

        // Full level

        /// <summary>
        /// Health status from the FabrAgentProxy.
        /// Only populated when detail level is Full.
        /// </summary>
        [Id(11)]
        public ProxyHealthStatus? ProxyHealth { get; init; }

        /// <summary>
        /// List of active stream names.
        /// Only populated when detail level is Full.
        /// </summary>
        [Id(12)]
        public List<string>? ActiveStreams { get; init; }

        /// <summary>
        /// Diagnostic information.
        /// Only populated when detail level is Full.
        /// </summary>
        [Id(13)]
        public Dictionary<string, string>? Diagnostics { get; init; }
    }
}
