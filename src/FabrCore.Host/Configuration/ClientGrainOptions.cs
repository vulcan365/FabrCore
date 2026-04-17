namespace FabrCore.Host.Configuration
{
    /// <summary>
    /// Client grain runtime knobs bound from configuration section <c>FabrCore:ClientGrain</c>.
    /// </summary>
    public class ClientGrainOptions
    {
        public const string SectionName = "FabrCore:ClientGrain";

        /// <summary>
        /// Maximum age of a pending (undelivered) message before it is discarded
        /// when a client reconnects. Default 1 hour.
        /// </summary>
        public TimeSpan PendingMessageMaxAge { get; set; } = TimeSpan.FromHours(1);
    }
}
