using Orleans;

namespace Fabr.Core
{
    /// <summary>
    /// Specifies the level of detail to include in health status responses.
    /// </summary>
    [GenerateSerializer]
    public enum HealthDetailLevel
    {
        /// <summary>
        /// Basic health information: overall status and configuration state only.
        /// </summary>
        [Id(0)]
        Basic = 0,

        /// <summary>
        /// Detailed health information: includes Basic plus agent type, uptime,
        /// message counts, timer/reminder counts, and full agent configuration.
        /// </summary>
        [Id(1)]
        Detailed = 1,

        /// <summary>
        /// Full health information: includes Detailed plus proxy health,
        /// active streams, and diagnostic information.
        /// </summary>
        [Id(2)]
        Full = 2
    }
}
