namespace FabrCore.Core
{
    /// <summary>
    /// Well-known FabrCore configuration keys and section names. All FabrCore
    /// configuration lives under the single <c>FabrCore</c> root element.
    /// </summary>
    public static class FabrCoreConfigurationKeys
    {
        /// <summary>Absolute base URL clients use to reach the FabrCore host.</summary>
        public const string HostUrl = "FabrCore:HostUrl";

        /// <summary>
        /// API key used by remote SDK processes to authenticate privileged Host API requests.
        /// </summary>
        public const string AdminApiKey = "FabrCore:AdminAuthentication:ApiKey";

        /// <summary>Orleans cluster configuration section.</summary>
        public const string OrleansSection = "FabrCore:Orleans";

        /// <summary>Azure Storage clustering provider sub-section.</summary>
        public const string OrleansAzureStorageSection = "FabrCore:Orleans:AzureStorage";

        /// <summary>File storage settings section.</summary>
        public const string FileStorageSection = "FabrCore:FileStorage";

        /// <summary>
        /// Opt-in flag ("true"/"false") that makes the SDK stamp outbound LLM requests with
        /// <see cref="AttributionHeaders"/> so an OpenAI-compatible gateway can attribute
        /// usage per agent. Default off.
        /// </summary>
        public const string EmitAttributionHeaders = "FabrCore:EmitAttributionHeaders";
    }
}
