namespace FabrCore.Services.Contracts.Capabilities;

public sealed class ClusterCapabilityDocument
{
    public const string CurrentApiVersion = "1";

    public string ApiVersion { get; set; } = CurrentApiVersion;

    public string HostVersion { get; set; } = string.Empty;

    public List<ClusterServiceCapability> Services { get; set; } = [];

    public List<string> BlueprintExtensions { get; set; } = [];
}

public sealed class ClusterServiceCapability
{
    public string Name { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string? ApiVersion { get; set; }

    public List<string> Features { get; set; } = [];
}
