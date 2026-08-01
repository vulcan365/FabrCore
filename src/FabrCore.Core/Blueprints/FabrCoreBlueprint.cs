using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Core.Blueprints;

/// <summary>
/// Canonical, versionable FabrCore configuration document. Package-owned sections are
/// represented as top-level JSON extension properties through <see cref="Extensions"/>.
/// </summary>
public class FabrCoreBlueprint
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Version { get; set; }

    public List<AgentConfiguration> Agents { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extensions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BlueprintExpansionContext
{
    public required string PrincipalId { get; init; }

    public required FabrCoreBlueprint Blueprint { get; init; }
}

public sealed class BlueprintExpansion
{
    public List<AgentConfiguration> Agents { get; set; } = [];
}

/// <summary>
/// Expands one package-owned blueprint extension into normal agent configurations.
/// </summary>
public interface IBlueprintExpander
{
    string ExtensionKey { get; }

    ValueTask<BlueprintExpansion> ExpandAsync(
        BlueprintExpansionContext context,
        JsonElement extension,
        CancellationToken cancellationToken = default);
}
