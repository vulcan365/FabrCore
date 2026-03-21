namespace FabrCore.Host.Configuration
{
    /// <summary>
    /// Well-known Orleans provider names required by FabrCore.
    /// When configuring Orleans manually with <see cref="FabrCoreSiloBuilderExtensions.AddFabrCore"/>,
    /// you must register providers with these names.
    /// </summary>
    public static class FabrCoreOrleansConstants
    {
        /// <summary>
        /// Grain storage provider name used by all FabrCore grains (AgentGrain, ClientGrain, AgentManagementGrain).
        /// </summary>
        public const string StorageProviderName = "fabrcoreStorage";

        /// <summary>
        /// Grain storage provider name required for Orleans streaming pub/sub state.
        /// </summary>
        /// <remarks>
        /// Orleans requires this to be "PubSubStore" (the default) or match the stream provider name.
        /// This is an Orleans convention and cannot be freely renamed.
        /// </remarks>
        public const string PubSubStoreName = "PubSubStore";

        /// <summary>
        /// Stream provider name used by FabrCore for agent-to-agent and agent-to-client communication.
        /// </summary>
        public const string StreamProviderName = "fabrcoreStreams";
    }
}
