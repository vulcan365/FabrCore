namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// Indicates the direction of a monitored message relative to the recording agent.
    /// </summary>
    public enum MessageDirection
    {
        /// <summary>Message received by the agent.</summary>
        Inbound,

        /// <summary>Message sent by the agent.</summary>
        Outbound
    }
}
