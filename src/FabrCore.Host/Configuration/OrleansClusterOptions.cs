namespace Fabr.Host.Configuration
{
    /// <summary>
    /// Configuration options for Orleans cluster providers.
    /// </summary>
    public class OrleansClusterOptions
    {
        /// <summary>
        /// Configuration section name in appsettings.json.
        /// </summary>
        public const string SectionName = "Orleans";

        /// <summary>
        /// The cluster ID used for Orleans membership. All silos in the same cluster must use the same ClusterId.
        /// </summary>
        public string ClusterId { get; set; } = "fabr-cluster";

        /// <summary>
        /// The service ID used for Orleans. All silos providing the same service must use the same ServiceId.
        /// </summary>
        public string ServiceId { get; set; } = "fabr-service";

        /// <summary>
        /// The clustering mode to use. Defaults to Localhost for development.
        /// </summary>
        public ClusteringMode ClusteringMode { get; set; } = ClusteringMode.Localhost;

        /// <summary>
        /// Connection string for clustering (membership). Used by SqlServer and AzureStorage modes.
        /// For SqlServer: "Server=...;Database=...;..."
        /// For AzureStorage: "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=..."
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>
        /// Connection string for grain storage. If not specified, uses ConnectionString.
        /// </summary>
        public string? StorageConnectionString { get; set; }

        /// <summary>
        /// Gets the effective storage connection string (StorageConnectionString if set, otherwise ConnectionString).
        /// </summary>
        public string? EffectiveStorageConnectionString => StorageConnectionString ?? ConnectionString;
    }

    /// <summary>
    /// Specifies the Orleans clustering mode/provider to use.
    /// </summary>
    public enum ClusteringMode
    {
        /// <summary>
        /// In-memory localhost clustering for development. Not suitable for production or multi-silo deployments.
        /// </summary>
        Localhost,

        /// <summary>
        /// SQL Server (ADO.NET) clustering for production Kubernetes deployments.
        /// Requires Orleans schema tables to be created in the database.
        /// </summary>
        SqlServer,

        /// <summary>
        /// Azure Storage Table clustering for Azure deployments.
        /// </summary>
        AzureStorage
    }
}
