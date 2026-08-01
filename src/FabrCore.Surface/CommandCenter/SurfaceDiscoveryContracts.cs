namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceDiscoveryRegistryMethod
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

public sealed class SurfaceDiscoveryRegistryEntry
{
    public string TypeName { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public string? Description { get; set; }

    public string? Capabilities { get; set; }

    public List<string> Notes { get; set; } = [];

    public List<SurfaceDiscoveryRegistryMethod> Methods { get; set; } = [];
}

public sealed class SurfaceDiscoveryRegistryCollision
{
    public string Alias { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public List<string> Types { get; set; } = [];
}

public sealed class SurfaceDiscoveryResponse
{
    public List<SurfaceDiscoveryRegistryEntry> Agents { get; set; } = [];

    public List<SurfaceDiscoveryRegistryEntry> Models { get; set; } = [];

    public List<SurfaceDiscoveryRegistryEntry> Plugins { get; set; } = [];

    public List<SurfaceDiscoveryRegistryEntry> Tools { get; set; } = [];

    public List<SurfaceDiscoveryRegistryCollision>? Collisions { get; set; }
}
