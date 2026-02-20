using Orleans;

namespace Fabr.Core
{
    /// <summary>
    /// Represents the overall health state of a component.
    /// </summary>
    [GenerateSerializer]
    public enum HealthState
    {
        /// <summary>
        /// Component is healthy and operating normally.
        /// </summary>
        [Id(0)]
        Healthy = 0,

        /// <summary>
        /// Component is operational but experiencing issues.
        /// </summary>
        [Id(1)]
        Degraded = 1,

        /// <summary>
        /// Component is not operational.
        /// </summary>
        [Id(2)]
        Unhealthy = 2,

        /// <summary>
        /// Component has not been configured.
        /// </summary>
        [Id(3)]
        NotConfigured = 3
    }
}
