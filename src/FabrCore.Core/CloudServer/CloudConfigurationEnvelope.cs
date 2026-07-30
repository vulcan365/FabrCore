using FabrCore.Core.Blueprints;

namespace FabrCore.Core.CloudServer;

/// <summary>
/// Response body of the cloud server configuration endpoint. Wraps the standard
/// <see cref="FabrCoreConfiguration"/> payload with versioning metadata so hosts can cache
/// and refresh efficiently.
/// </summary>
public sealed class CloudConfigurationEnvelope
{
    /// <summary>Gets or sets the envelope schema version. See <see cref="CloudServerProtocol.CurrentSchemaVersion"/>.</summary>
    public int SchemaVersion { get; set; } = CloudServerProtocol.CurrentSchemaVersion;

    /// <summary>
    /// Gets or sets the server-defined opaque version of this configuration. Doubles as the
    /// ETag value: hosts echo it in If-None-Match and the server responds 304 when unchanged.
    /// </summary>
    public string ConfigurationVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets when the server produced this envelope.</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>Gets or sets the model configurations and API keys for the requesting cluster.</summary>
    public FabrCoreConfiguration Configuration { get; set; } = new();

    /// <summary>
    /// Gets or sets optional flat IConfiguration key/value settings (for example
    /// "FabrCore:Host:WebSocketPath"). Reserved for future use — current hosts ignore it, so
    /// servers may omit or populate it without breaking compatibility.
    /// </summary>
    public Dictionary<string, string?>? Settings { get; set; }

    /// <summary>
    /// Gets or sets canonical blueprint deployments distributed with this configuration.
    /// This additive field is optional for v1 servers and clients.
    /// </summary>
    public List<CloudBlueprintDeployment> Blueprints { get; set; } = [];
}

/// <summary>One principal-scoped blueprint delivered by a cloud configuration server.</summary>
public sealed class CloudBlueprintDeployment
{
    public string PrincipalId { get; set; } = string.Empty;
    public FabrCoreBlueprint Blueprint { get; set; } = new();
    public bool ApplyOnRefresh { get; set; } = true;
}
