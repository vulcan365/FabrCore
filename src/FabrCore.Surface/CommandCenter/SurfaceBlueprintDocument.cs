using FabrCore.Core;
using FabrCore.Core.Blueprints;
using FabrCore.Surface.Ai.Squads;
using System.Text.Json;

namespace FabrCore.Surface.CommandCenter;

/// <summary>
/// Backward-compatible Surface view of the canonical <see cref="FabrCoreBlueprint"/>.
/// The typed Squads property is serialized as the canonical top-level "squads" extension;
/// hosts deserialize that property into <see cref="FabrCoreBlueprint.Extensions"/>.
/// </summary>
public sealed class SurfaceBlueprintDocument : FabrCoreBlueprint
{
    public List<SurfaceSquadDefinition> Squads { get; set; } = [];
}

public sealed class SurfaceBlueprintApplyResult
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public int TotalRequested { get; set; }

    public int SuccessCount { get; set; }

    public int FailureCount { get; set; }

    public List<AgentHealthStatus> Results { get; set; } = [];

    public int SquadsCreated { get; set; }

    public int SquadsSkipped { get; set; }

    public int AgentConfigurationsRequested { get; set; }
}

public sealed class SurfaceAgentBlueprintRequest
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public List<AgentConfiguration> Agents { get; set; } = [];
}

internal sealed class SurfaceAgentBlueprintResponse
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public int TotalRequested { get; set; }

    public int SuccessCount { get; set; }

    public int FailureCount { get; set; }

    public List<SurfaceAgentBlueprintResult> Results { get; set; } = [];
}

internal sealed class SurfaceAgentBlueprintResult
{
    public string? Handle { get; set; }

    public JsonElement State { get; set; }

    public bool IsConfigured { get; set; }

    public string? Message { get; set; }

    public string? AgentType { get; set; }
}
