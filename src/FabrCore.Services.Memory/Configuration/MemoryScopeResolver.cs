using FabrCore.Core;
using FabrCore.Sdk;

namespace FabrCore.Services.Memory.Configuration;

/// <summary>
/// Resolves the memory scope key for an agent from its configuration.
/// The scope defaults to the agent's own handle (isolated memory); config can
/// point the agent at a named shared scope instead.
/// </summary>
public static class MemoryScopeResolver
{
    /// <summary>The plugin alias whose settings are consulted.</summary>
    public const string PluginAlias = "agent-memory";

    /// <summary>The setting / argument key that names the memory scope.</summary>
    public const string MemoryScopeKey = "MemoryScope";

    /// <summary>
    /// Resolve the scope key. Precedence:
    /// explicit value → plugin setting "MemoryScope" → Args["MemoryScope"] →
    /// Args["AgentHandle"] (legacy) → the agent handle (isolated default).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no scope can be resolved.</exception>
    public static string Resolve(AgentConfiguration config, string? explicitScope = null)
    {
        // Plugin settings live in Args under "<alias>:<key>" — GetPluginSetting
        // does not tolerate a null Args dictionary, so guard it here.
        var pluginSetting = config.Args is null
            ? null
            : config.GetPluginSetting(PluginAlias, MemoryScopeKey);

        var scope = explicitScope
            ?? pluginSetting
            ?? config.Args?.GetValueOrDefault(MemoryScopeKey)
            ?? config.Args?.GetValueOrDefault("AgentHandle")
            ?? config.Handle;

        if (string.IsNullOrWhiteSpace(scope))
            throw new InvalidOperationException(
                "Could not resolve a memory scope for this agent. Provide one via the " +
                $"'{PluginAlias}' plugin setting '{MemoryScopeKey}', Args[\"{MemoryScopeKey}\"], " +
                "or ensure the agent configuration has a Handle (the default isolated scope).");

        return scope.Trim();
    }
}
