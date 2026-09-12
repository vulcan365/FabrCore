namespace FabrCore.Services.Contracts.Capabilities;

public sealed class ClusterCapabilityDocument
{
    public const string CurrentApiVersion = "2";

    public string ApiVersion { get; set; } = CurrentApiVersion;

    public string HostVersion { get; set; } = string.Empty;

    public List<ClusterServiceCapability> Services { get; set; } = [];

    public List<string> BlueprintExtensions { get; set; } = [];

    public int? MaxRequestBodyBytes { get; set; }

    public string DataScope { get; set; } = "cluster";
}

public sealed class ClusterServiceCapability
{
    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string? ApiVersion { get; set; }

    public List<string> Features { get; set; } = [];

    public string DataScope { get; set; } = "cluster";

    public int? MaxRequestBodyBytes { get; set; }

    public bool Available { get; set; } = true;

    public string? UnavailableReason { get; set; }
}
