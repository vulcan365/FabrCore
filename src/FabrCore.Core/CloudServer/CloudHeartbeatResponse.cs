namespace FabrCore.Core.CloudServer;

/// <summary>
/// Response body of the cloud server heartbeat endpoint. All members are optional — an empty
/// object is a valid response. This is the seam for server-initiated actions without a
/// persistent connection: future protocol versions may add members (for example a command
/// list) additively.
/// </summary>
public sealed class CloudHeartbeatResponse
{
    /// <summary>
    /// Gets or sets whether the server wants the host to refresh its configuration
    /// immediately instead of waiting for the next scheduled refresh.
    /// </summary>
    public bool? RefreshRequested { get; set; }

    /// <summary>
    /// Gets or sets the latest configuration version available for this cluster/environment,
    /// letting hosts detect staleness from the heartbeat alone.
    /// </summary>
    public string? LatestConfigurationVersion { get; set; }
}
