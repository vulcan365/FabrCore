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

    public List<string> AllowedActionTypes { get; set; } =
    [
        "Action.Execute",
        "Action.Submit",
        "Action.OpenUrl",
        "Action.ShowCard",
        "Action.ToggleVisibility"
    ];

    public List<string> AllowedActionVerbs { get; set; } = [];

    public bool? AllowAnyActionVerb { get; set; }

    public bool? AllowUnknownTargetAgents { get; set; }

    public List<string> AllowedTargetAgents { get; set; } = [];

    public bool? EnableDiagnostics { get; set; }

    public List<SurfaceRequiredActionDefinition> RequiredActions { get; set; } = [];
}

public sealed class SurfaceRequiredActionDefinition
{
    public string AppliesTo { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Verb { get; set; } = string.Empty;

    public string RouteTo { get; set; } = "agent";

    public string? TargetAgent { get; set; }

    public string IdField { get; set; } = "id";

    public string? MessageTemplate { get; set; }
}
