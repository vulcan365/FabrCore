namespace FabrCore.Surface.Services;

public sealed class SurfaceOptions
{
    public string? DefinitionFilePath { get; set; }

    public string? DefaultSurfaceDefinitionName { get; set; }

    public string? DefaultPlanningModelName { get; set; }

    public string MaxAdaptiveCardVersion { get; set; } = "1.6";

    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    public int MaxDepth { get; set; } = 64;

    public bool AllowHttpUrls { get; set; }

    public HashSet<string> AllowedActionTypes { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Action.Execute",
        "Action.Submit",
        "Action.OpenUrl",
        "Action.ShowCard",
        "Action.ToggleVisibility"
    };

    public HashSet<string> AllowedActionVerbs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AllowAnyActionVerb { get; set; } = true;

    public bool AllowUnknownTargetAgents { get; set; }

    public HashSet<string> AllowedTargetAgents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool EnableDiagnostics { get; set; }
}
