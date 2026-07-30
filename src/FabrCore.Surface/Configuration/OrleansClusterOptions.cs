namespace FabrCore.Surface.Configuration;

/// <summary>
/// Orleans client connection options for a FabrCore Surface.
/// Must match the FabrCore host cluster configuration.
/// </summary>
public sealed class OrleansClusterOptions
{
    public const string SectionName = "Orleans";

    public string ClusterId { get; set; } = "fabrcore-cluster";

    public string ServiceId { get; set; } = "fabrcore-service";

    public ClusteringMode ClusteringMode { get; set; } = ClusteringMode.Localhost;

    public string? ConnectionString { get; set; }

    public int ConnectionRetryCount { get; set; } = 5;

    public TimeSpan ConnectionRetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan GatewayListRefreshPeriod { get; set; } = TimeSpan.FromSeconds(30);
}

public enum ClusteringMode
{
    Localhost,
    SqlServer,
    AzureStorage
}
