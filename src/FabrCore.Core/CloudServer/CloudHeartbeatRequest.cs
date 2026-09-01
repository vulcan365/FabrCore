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

    /// <summary>
    /// Gets or sets installed service API versions, keyed by capability name.
    /// This additive field is safe for schema-v1 servers that ignore unknown members.
    /// </summary>
    public Dictionary<string, string> Capabilities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the configuration version whose <c>settings</c> map this host has applied,
    /// when cloud-delivered settings are enabled. Additive: schema-v1 servers ignore it.
    /// </summary>
    public string? AppliedSettingsVersion { get; set; }

    /// <summary>
    /// Gets or sets the settings keys whose cloud value differs from the value this process
    /// started with and whose consumers cannot observe a change without a restart. Servers use
    /// this to show operators that a published change is stored but not yet in effect. Additive:
    /// schema-v1 servers ignore it.
    /// </summary>
    public List<string>? PendingRestartSettings { get; set; }

    /// <summary>Gets or sets when the host produced this heartbeat.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
