namespace FabrCore.Host.Configuration
{
    /// <summary>
    /// Principal grain runtime knobs bound from configuration section <c>FabrCore:PrincipalGrain</c>.
    /// </summary>
    public class PrincipalGrainOptions
    {
        public const string SectionName = "FabrCore:PrincipalGrain";

        /// <summary>
        /// Maximum age of a pending (undelivered) message before it is discarded
        /// when a principal reconnects. Default 1 hour.
        /// </summary>
        public TimeSpan PendingMessageMaxAge { get; set; } = TimeSpan.FromHours(1);
    }
}
