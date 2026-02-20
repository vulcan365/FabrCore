namespace FabrCore.Client.Configuration
{
    /// <summary>
    /// Configuration options for Orleans cluster providers.
    /// Must match the configuration used by the FabrCore.Host server.
    /// </summary>
    public class OrleansClusterOptions
    {
        /// <summary>
        /// Configuration section name in appsettings.json.
        /// </summary>
        public const string SectionName = "Orleans";

        /// <summary>
        /// The cluster ID used for Orleans membership. Must match the server's ClusterId.
        /// </summary>
        public string ClusterId { get; set; } = "fabrcore-cluster";

        /// <summary>
        /// The service ID used for Orleans. Must match the server's ServiceId.
        /// </summary>
        public string ServiceId { get; set; } = "fabrcore-service";

        /// <summary>
        /// The clustering mode to use. Must match the server's clustering mode.
        /// </summary>
        public ClusteringMode ClusteringMode { get; set; } = ClusteringMode.Localhost;

        /// <summary>
        /// Connection string for clustering (membership). Used by SqlServer and AzureStorage modes.
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>
        /// Maximum number of connection retry attempts when connecting to the Orleans cluster.
        /// Default is 5 attempts. Set to 0 for no retries (fail immediately).
        /// </summary>
        public int ConnectionRetryCount { get; set; } = 5;

        /// <summary>
        /// Delay between connection retry attempts.
        /// Default is 3 seconds.
        /// </summary>
        public TimeSpan ConnectionRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Total timeout for the gateway connection attempt.
        /// Default is 30 seconds.
        /// </summary>
        public TimeSpan GatewayListRefreshPeriod { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Specifies the Orleans clustering mode/provider to use.
    /// </summary>
    public enum ClusteringMode
    {
        /// <summary>
        /// In-memory localhost clustering for development.
        /// </summary>
        Localhost,

        /// <summary>
        /// SQL Server (ADO.NET) clustering for production Kubernetes deployments.
        /// </summary>
        SqlServer,

        /// <summary>
        /// Azure Storage Table clustering for Azure deployments.
        /// </summary>
        AzureStorage
    }
}
