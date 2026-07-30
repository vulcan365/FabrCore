using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Configuration;

/// <summary>
/// Named Adaptive Card Surface generation profile loaded from <c>fabrcore-surface.json</c>.
/// </summary>
public sealed class SurfaceDefinition
{
    public string Name { get; set; } = "default";

    public string Description { get; set; } = string.Empty;

    public string? PlanningModelName { get; set; }

    public string? SystemPrompt { get; set; }

    public string MaxAdaptiveCardVersion { get; set; } = "1.6";

    public int? MaxPayloadBytes { get; set; }

    public int? MaxDepth { get; set; }

    public bool? AllowHttpUrls { get; set; }

    public List<string> AllowedActionTypes { get; set; } = new(AdaptiveCardActionTypes.Defaults);

    public bool? AllowUnknownTargetAgents { get; set; }

    public List<string> AllowedTargetAgents { get; set; } = [];

    public bool? EnableDiagnostics { get; set; }
}
