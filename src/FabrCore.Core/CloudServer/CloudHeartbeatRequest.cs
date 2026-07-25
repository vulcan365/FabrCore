namespace FabrCore.Core.CloudServer;

/// <summary>
/// Body of the cloud server heartbeat endpoint. Sent periodically by each host instance
/// (one heartbeat per silo — <see cref="HostInstanceId"/> disambiguates) so the server can
/// track cluster liveness and detect stale configuration.
/// </summary>
public sealed class CloudHeartbeatRequest
{
    /// <summary>Gets or sets the heartbeat schema version.</summary>
    public int SchemaVersion { get; set; } = CloudServerProtocol.CurrentSchemaVersion;

    /// <summary>Gets or sets the cluster identifier (mirrors the request header).</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>Gets or sets the host's environment name (for example "Production").</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>Gets or sets the Orleans service identifier.</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Gets or sets a stable identifier for this host instance/silo.</summary>
    public string HostInstanceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the FabrCore host assembly version.</summary>
    public string HostVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the configuration version this host is currently running with, if any.</summary>
    public string? AppliedConfigurationVersion { get; set; }

    /// <summary>Gets or sets the number of active Orleans gateways observed by this host.</summary>
    public int ActiveGatewayCount { get; set; }

    /// <summary>Gets or sets when the host produced this heartbeat.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
