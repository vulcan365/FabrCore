namespace FabrCore.Core
{
    /// <summary>
    /// Specifies the level of detail to include in health status responses.
    /// </summary>
    public enum HealthDetailLevel
    {
        /// <summary>
        /// Basic health information: overall status and configuration state only.
        /// </summary>
        Basic = 0,

        /// <summary>
        /// Detailed health information: includes Basic plus agent type, uptime,
        /// message counts, timer/reminder counts, and full agent configuration.
        /// </summary>
        Detailed = 1,

        /// <summary>
        /// Full health information: includes Detailed plus proxy health,
        /// active streams, and diagnostic information.
        /// </summary>
        Full = 2
    }
}
