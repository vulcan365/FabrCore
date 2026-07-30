using FabrCore.Surface.Services;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfacePreferences
{
    public int Version { get; set; } = 1;

    public bool ShowHiddenAgents { get; set; }

    public bool ShowRunningAgents { get; set; }

    public HashSet<string> SurfaceAgentHandles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SurfacePreferences FromDefaults(SurfaceOptions defaults)
        => new()
        {
            ShowHiddenAgents = defaults.ShowHiddenAgentsByDefault,
            ShowRunningAgents = defaults.ShowRunningAgentsByDefault,
            SurfaceAgentHandles = new HashSet<string>(
                defaults.DefaultSurfaceAgentHandles.Where(handle => !string.IsNullOrWhiteSpace(handle)),
                StringComparer.OrdinalIgnoreCase)
        };
}
