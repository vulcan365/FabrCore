namespace FabrCore.Host.Configuration
{
    /// <summary>
    /// Agent grain runtime knobs bound from configuration section <c>FabrCore:AgentGrain</c>.
    /// </summary>
    public class AgentGrainOptions
    {
        public const string SectionName = "FabrCore:AgentGrain";

        /// <summary>
        /// Interval between heartbeat status messages sent to the original caller
        /// while an OnMessage handler is running. Reasoning/long-thinking models
        /// may want a larger value. Default 3 seconds.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Number of recent message latencies retained in the rolling reservoir
        /// used to compute p50/p95/p99 for agent health. Default 256.
        /// </summary>
        public int LatencyReservoirCapacity { get; set; } = 256;
    }
}
