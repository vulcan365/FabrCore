namespace FabrCore.Core
{
    /// <summary>
    /// Represents the overall health state of a component.
    /// </summary>
    public enum HealthState
    {
        /// <summary>
        /// Component is healthy and operating normally.
        /// </summary>
        Healthy = 0,

        /// <summary>
        /// Component is operational but experiencing issues.
        /// </summary>
        Degraded = 1,

        /// <summary>
        /// Component is not operational.
        /// </summary>
        Unhealthy = 2,

        /// <summary>
        /// Component has not been configured.
        /// </summary>
        NotConfigured = 3
    }
}
