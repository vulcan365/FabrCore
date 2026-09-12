namespace FabrCore.Services.Memory.Configuration;

public enum MemoryAdminClientMode
{
    Auto,
    Local,
    Remote
}

public sealed class MemoryAdminClientOptions
{
    public MemoryAdminClientMode Mode { get; set; } = MemoryAdminClientMode.Auto;

    /// <summary>The FabrCore host base URL, without the Memory administration API path.</summary>
    public string? BaseAddress { get; set; }

    /// <summary>Cluster-scoped API key sent as a Bearer token.</summary>
    public string? ApiKey { get; set; }
}
