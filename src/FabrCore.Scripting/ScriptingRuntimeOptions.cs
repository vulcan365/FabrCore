namespace FabrCore.Scripting;

public sealed class ScriptingRuntimeOptions
{
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FabrCore", "Scripting", "environments");
    public string WorkingDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "FabrCore", "Scripting", "executions");
    public string DotnetPath { get; set; } = "dotnet";
    public string? NuGetConfigPath { get; set; }
    public TimeSpan PreparationTimeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Set false after prewarming to require a prepared environment (no SDK or restore at startup).</summary>
    public bool AllowEnvironmentPreparation { get; set; } = true;
    public int MaxCodeCharacters { get; set; } = 131_072;
    public int MaxInputCharacters { get; set; } = 1_048_576;
}
