using System.Text;
using System.Text.RegularExpressions;

namespace FabrCore.Surface.Ai.Swarm;

public static partial class SurfaceSquadHandleBuilder
{
    public static string ToSlug(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim().ToLowerInvariant();
        normalized = NonHandleCharacters().Replace(normalized, "-");
        normalized = RepeatedDashes().Replace(normalized, "-").Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "squad";
        }

        return normalized;
    }

    public static string BuildOrchestratorAlias(string squadSlug)
        => $"squad-{ToSlug(squadSlug)}";

    public static string BuildPlannerAlias(string squadSlug)
        => $"{BuildOrchestratorAlias(squadSlug)}-planner";

    public static string BuildMemberAlias(string squadSlug, string agentName)
        => $"{BuildOrchestratorAlias(squadSlug)}-{ToSlug(agentName)}";

    public static string Qualify(string principalHandle, string alias)
        => string.IsNullOrWhiteSpace(principalHandle) ? alias : $"{principalHandle}:{alias}";

    public static string DisplayNameFromHandle(string handle)
    {
        var colon = handle.IndexOf(':', StringComparison.Ordinal);
        var alias = colon >= 0 && colon + 1 < handle.Length ? handle[(colon + 1)..] : handle;
        var builder = new StringBuilder(alias.Length);
        var capitalize = true;
        foreach (var ch in alias)
        {
            if (ch is '-' or '_' or ':')
            {
                builder.Append(' ');
                capitalize = true;
                continue;
            }

            builder.Append(capitalize ? char.ToUpperInvariant(ch) : ch);
            capitalize = false;
        }

        return builder.ToString();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonHandleCharacters();

    [GeneratedRegex("-+")]
    private static partial Regex RepeatedDashes();
}
