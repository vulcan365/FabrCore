using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Swarm;

namespace FabrCore.Surface.Ai.Orchestration;

public sealed class SurfaceSquadAgentCapability
{
    public string Name { get; set; } = string.Empty;

    public string Handle { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Plugins { get; set; } = [];

    public List<string> Tools { get; set; } = [];

    public string? Notes { get; set; }

    public bool IsConfigured { get; set; }

    public string? UnavailableReason { get; set; }
}

public static class SurfaceSquadAgentCapabilityProjection
{
    private const int DescriptionCap = 500;

    public static SurfaceSquadAgentCapability Build(
        SurfaceSquadAgent squadAgent,
        RegistryEntry? registryEntry,
        AgentHealthStatus? health,
        string? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(squadAgent);

        var description = !string.IsNullOrWhiteSpace(squadAgent.Description)
            ? squadAgent.Description!
            : !string.IsNullOrWhiteSpace(health?.Configuration?.Description)
                ? health!.Configuration!.Description!
                : !string.IsNullOrWhiteSpace(registryEntry?.Description)
                    ? registryEntry!.Description
                    : $"Agent {squadAgent.Name}";

        if (!string.IsNullOrWhiteSpace(registryEntry?.Capabilities))
        {
            description = string.IsNullOrWhiteSpace(description)
                ? registryEntry!.Capabilities
                : $"{description}\nCapabilities: {registryEntry!.Capabilities}";
        }

        if (description.Length > DescriptionCap)
        {
            description = description[..DescriptionCap] + $"... [truncated, {description.Length - DescriptionCap} more chars]";
        }

        var notes = registryEntry?.Notes is { Count: > 0 } noteList
            ? string.Join(Environment.NewLine, noteList.Select(note => $"- {note}"))
            : null;

        return new SurfaceSquadAgentCapability
        {
            Name = squadAgent.Name,
            Handle = squadAgent.Handle,
            AgentType = squadAgent.AgentType,
            Description = description,
            Plugins = health?.Configuration?.Plugins is { Count: > 0 } plugins
                ? [.. plugins]
                : [],
            Tools = health?.Configuration?.Tools is { Count: > 0 } tools
                ? [.. tools]
                : [],
            Notes = notes,
            IsConfigured = health?.IsConfigured == true,
            UnavailableReason = unavailableReason
        };
    }
}
