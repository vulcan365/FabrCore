using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Surface.Configuration;

internal static class SurfaceDefinitionFileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };

    public static IReadOnlyList<SurfaceDefinition> Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return [];
        }

        var json = File.ReadAllText(filePath);
        var file = JsonSerializer.Deserialize<SurfaceDefinitionFile>(json, JsonOptions);
        return file?.GetDefinitions().ToList() ?? [];
    }

    public static SurfaceDefinition? LoadByName(string filePath, string definitionName)
        => Load(filePath).FirstOrDefault(d => string.Equals(d.Name, definitionName, StringComparison.OrdinalIgnoreCase));
}
