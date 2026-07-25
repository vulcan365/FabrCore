namespace FabrCore.Core.CloudServer;

/// <summary>
/// Constants for the FabrCore cloud server protocol — the vendor-neutral REST contract a
/// remote configuration server implements so FabrCore hosts can pull their configuration
/// (and report heartbeats) instead of reading a local fabrcore.json. See
/// docs/cloud-server-protocol.md for the full specification.
/// </summary>
public static class CloudServerProtocol
{
    /// <summary>The highest envelope schema version this build understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Relative path of the configuration endpoint (GET).</summary>
    public const string ConfigurationPath = "/fabrcore-cloud/v1/configuration";

    /// <summary>Relative path of the heartbeat endpoint (POST).</summary>
    public const string HeartbeatPath = "/fabrcore-cloud/v1/heartbeat";

    /// <summary>Request header carrying the cluster identifier.</summary>
    public const string ClusterIdHeader = "X-FabrCore-Cluster-Id";

    /// <summary>
    /// Request header carrying the host's environment name (for example "Production"),
    /// enabling appsettings-style base + environment-overlay configuration layering on the
    /// server. Hosts default this to <c>IHostEnvironment.EnvironmentName</c>.
    /// </summary>
    public const string EnvironmentHeader = "X-FabrCore-Environment";
}
