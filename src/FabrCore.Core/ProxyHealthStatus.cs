using Orleans;

namespace FabrCore.Core
{
    /// <summary>
    /// Health status information from the FabrCoreAgentProxy.
    /// </summary>
    [GenerateSerializer]
    public record ProxyHealthStatus
    {
        /// <summary>
        /// Health state of the proxy.
        /// </summary>
        [Id(0)]
        public required HealthState State { get; init; }

        /// <summary>
        /// Whether the proxy has been initialized.
        /// </summary>
        [Id(1)]
        public required bool IsInitialized { get; init; }

        /// <summary>
        /// The proxy type name.
        /// </summary>
        [Id(2)]
        public string? ProxyTypeName { get; init; }

        /// <summary>
        /// When the proxy was initialized.
        /// </summary>
        [Id(3)]
        public DateTime? InitializedAt { get; init; }

        /// <summary>
        /// Custom metrics provided by derived proxy implementations.
        /// </summary>
        [Id(4)]
        public Dictionary<string, string>? CustomMetrics { get; init; }

        /// <summary>
        /// Human-readable message about the proxy health.
        /// </summary>
        [Id(5)]
        public string? Message { get; init; }
    }
}
