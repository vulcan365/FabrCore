using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace FabrCore.Scripting;

/// <summary>Developer-owned configuration. Script code cannot select its packages or execution limits.</summary>
public sealed class ScriptingEnvironmentBuilder
{
    private readonly Dictionary<string, string> packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> imports = ["System", "System.Linq", "System.Collections.Generic", "System.Text.Json", "System.Threading.Tasks"];
    private TimeSpan timeout = TimeSpan.FromSeconds(30);
    private long memoryLimit = 512L * 1024 * 1024;
    private int outputLimit = 32_768;
    private int artifactLimit = 1_048_576;
    private string instructions = "";

    public ScriptingEnvironmentBuilder AddPackage(string packageId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (packageId.Length > 100 || !Regex.IsMatch(packageId, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z"))
            throw new ArgumentException("Use a NuGet package ID, not a path or URL.", nameof(packageId));
        if (!NuGetVersion.TryParse(version, out var parsed))
            throw new ArgumentException("Specify an exact NuGet version; ranges and floating versions are not supported.", nameof(version));
        var normalized = parsed.ToNormalizedString();
        if (packageId.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Roslyn dependencies are supplied by the scripting worker.", nameof(packageId));
        if (packages.TryGetValue(packageId, out var previous) && previous != normalized)
            throw new ArgumentException($"Package '{packageId}' already has version '{previous}'.", nameof(version));
        packages[packageId.ToLowerInvariant()] = normalized;
        return this;
    }

    public ScriptingEnvironmentBuilder AddImports(params string[] namespaces)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        foreach (var name in namespaces)
        {
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z"))
                throw new ArgumentException("Imports must be namespace names.", nameof(namespaces));
            if (!imports.Contains(name, StringComparer.Ordinal)) imports.Add(name);
        }
        return this;
    }

    public ScriptingEnvironmentBuilder WithTimeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(value));
        timeout = value;
        return this;
    }

    /// <summary>Supervised worker working-set limit, not an OS-enforced process-tree memory quota.</summary>
    public ScriptingEnvironmentBuilder WithMemoryLimit(long bytes)
    {
        if (bytes < 64L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bytes));
        memoryLimit = bytes;
        return this;
    }

    public ScriptingEnvironmentBuilder WithOutputLimit(int characters)
    {
        if (characters < 256 || characters > 1_048_576) throw new ArgumentOutOfRangeException(nameof(characters));
        outputLimit = characters;
        return this;
    }

    public ScriptingEnvironmentBuilder WithArtifactLimit(int bytes)
    {
        if (bytes < 0 || bytes > 16_777_216) throw new ArgumentOutOfRangeException(nameof(bytes));
        artifactLimit = bytes;
        return this;
    }

    public ScriptingEnvironmentBuilder WithInstructions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        instructions = text;
        return this;
    }

    internal EnvironmentDefinition Build() => new(
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            packages.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)),
        imports.ToArray(), timeout, memoryLimit, outputLimit, artifactLimit, instructions);
}

internal sealed record EnvironmentDefinition(
    IReadOnlyDictionary<string, string> Packages, string[] Imports, TimeSpan Timeout,
    long MemoryLimitBytes, int OutputLimit, int ArtifactLimit, string Instructions);
