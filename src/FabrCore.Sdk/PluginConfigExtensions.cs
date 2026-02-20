using FabrCore.Core;

namespace FabrCore.Sdk;

public static class PluginConfigExtensions
{
    /// <summary>
    /// Gets all Args entries for a specific plugin using the "PluginAlias:Key" convention.
    /// Returns a dictionary with the prefix stripped from keys.
    /// </summary>
    public static Dictionary<string, string> GetPluginSettings(
        this AgentConfiguration config, string pluginAlias)
    {
        var prefix = pluginAlias + ":";
        return config.Args
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                kv => kv.Key.Substring(prefix.Length),
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a single plugin setting value, or null if not found.
    /// </summary>
    public static string? GetPluginSetting(
        this AgentConfiguration config, string pluginAlias, string key)
    {
        config.Args.TryGetValue($"{pluginAlias}:{key}", out var value);
        return value;
    }
}
