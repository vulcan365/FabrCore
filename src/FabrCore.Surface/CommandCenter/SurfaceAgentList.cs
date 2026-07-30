using FabrCore.Core;

namespace FabrCore.Surface.CommandCenter;

public static class SurfaceAgentList
{
    public static IReadOnlyList<SurfaceAgentSummary> Merge(
        string principalId,
        IEnumerable<TrackedAgentInfo> trackedAgents,
        IEnumerable<AgentInfo> sharedAgents,
        IEnumerable<string>? hiddenAgentTypes = null,
        IEnumerable<string>? hiddenAgentHandles = null,
        IEnumerable<string>? surfaceAgentHandles = null,
        bool includeHidden = false)
    {
        var agents = new Dictionary<string, SurfaceAgentSummary>(StringComparer.OrdinalIgnoreCase);
        var hiddenTypes = new HashSet<string>(hiddenAgentTypes ?? [], StringComparer.OrdinalIgnoreCase);
        var hiddenHandles = new HashSet<string>(hiddenAgentHandles ?? [], StringComparer.OrdinalIgnoreCase);
        var surfaceHandles = new HashSet<string>(surfaceAgentHandles ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var tracked in trackedAgents)
        {
            if (string.IsNullOrWhiteSpace(tracked.Handle))
            {
                continue;
            }

            var displayName = ToDisplayName(tracked.Handle, principalId);
            var isHidden = IsHidden(tracked.Handle, displayName, tracked.AgentType, principalId, hiddenTypes, hiddenHandles);
            if (isHidden && !includeHidden)
            {
                continue;
            }

            agents[tracked.Handle] = new SurfaceAgentSummary
            {
                Handle = tracked.Handle,
                DisplayName = displayName,
                AgentType = tracked.AgentType,
                Health = tracked.Health,
                IsHidden = isHidden,
                IsSurfaceAgent = MatchesHandle(tracked.Handle, displayName, principalId, surfaceHandles),
                IsShared = false
            };
        }

        foreach (var shared in sharedAgents)
        {
            if (string.IsNullOrWhiteSpace(shared.Key) || agents.ContainsKey(shared.Key))
            {
                continue;
            }

            var displayName = ToDisplayName(shared.Key, principalId);
            var isHidden = IsHidden(shared.Key, displayName, shared.AgentType, principalId, hiddenTypes, hiddenHandles);
            if (isHidden && !includeHidden)
            {
                continue;
            }

            agents[shared.Key] = new SurfaceAgentSummary
            {
                Handle = shared.Key,
                DisplayName = displayName,
                AgentType = shared.AgentType,
                IsHidden = isHidden,
                IsSurfaceAgent = MatchesHandle(shared.Key, displayName, principalId, surfaceHandles),
                IsShared = true
            };
        }

        return agents.Values
            .OrderBy(a => a.IsShared)
            .ThenBy(a => a.IsHidden)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToDisplayName(string handle, string principalId)
    {
        var ownerPrefix = $"{principalId}:";
        if (handle.StartsWith(ownerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return handle[ownerPrefix.Length..];
        }

        var separator = handle.IndexOf(':');
        return separator >= 0 && separator < handle.Length - 1
            ? handle[(separator + 1)..]
            : handle;
    }

    private static bool IsHidden(
        string handle,
        string displayName,
        string agentType,
        string principalId,
        HashSet<string> hiddenTypes,
        HashSet<string> hiddenHandles)
    {
        if (hiddenTypes.Contains(agentType))
        {
            return true;
        }

        if (hiddenHandles.Contains(handle) || hiddenHandles.Contains(displayName))
        {
            return true;
        }

        var ownerPrefix = $"{principalId}:";
        return hiddenHandles.Contains(handle.StartsWith(ownerPrefix, StringComparison.OrdinalIgnoreCase)
            ? handle[ownerPrefix.Length..]
            : handle);
    }

    private static bool MatchesHandle(
        string handle,
        string displayName,
        string principalId,
        HashSet<string> handles)
    {
        if (handles.Count == 0)
        {
            return false;
        }

        if (handles.Contains(handle) || handles.Contains(displayName))
        {
            return true;
        }

        var ownerPrefix = $"{principalId}:";
        return handles.Contains(handle.StartsWith(ownerPrefix, StringComparison.OrdinalIgnoreCase)
            ? handle[ownerPrefix.Length..]
            : handle);
    }
}
