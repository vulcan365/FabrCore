namespace FabrCore.Surface.Configuration;

/// <summary>
/// Host-level options for agent-side Surface planning services.
/// </summary>
public sealed class SurfaceAiOptions
{
    public string DefinitionFilePath { get; set; } = "fabrcore-surface.json";

    public string DefaultSurfaceDefinitionName { get; set; } = "default";

    public string? DefaultPlanningModelName { get; set; }

    public int MaxPromptCharacters { get; set; } = 12_000;

    public bool SendRenderMessages { get; set; } = true;
}
