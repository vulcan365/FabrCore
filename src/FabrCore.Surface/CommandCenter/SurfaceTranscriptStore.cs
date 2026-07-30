namespace FabrCore.Surface.CommandCenter;

internal sealed class SurfaceTranscriptStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<string, List<SurfaceTimelineItem>> timelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> dedupeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> unreadCounts = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Changed;

    public IReadOnlyList<SurfaceTimelineItem> GetTimeline(string? principalId)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return [];
        }

        lock (syncRoot)
        {
            return timelines.TryGetValue(principalId, out var timeline)
                ? timeline.ToList()
                : [];
        }
    }

    public bool Add(string principalId, SurfaceTimelineItem item, int maxTimelineItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(item);

        lock (syncRoot)
        {
            var timeline = GetOrCreateTimeline(principalId);
            var keys = GetOrCreateDedupeKeys(principalId);
            var dedupeKey = BuildDedupeKey(item);

            if (dedupeKey is not null && !keys.Add(dedupeKey))
            {
                return false;
            }

            timeline.Add(item);
            Trim(timeline, keys, maxTimelineItems);
            return true;
        }
    }

    public void NotifyChanged(string principalId)
    {
        if (!string.IsNullOrWhiteSpace(principalId))
        {
            Changed?.Invoke(principalId);
        }
    }

    public IReadOnlyDictionary<string, int> GetUnreadCounts(string? principalId)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        lock (syncRoot)
        {
            return unreadCounts.TryGetValue(principalId, out var ownerCounts)
                ? new Dictionary<string, int>(ownerCounts, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public int GetTotalUnreadCount(string? principalId)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return 0;
        }

        lock (syncRoot)
        {
            return unreadCounts.TryGetValue(principalId, out var ownerCounts)
                ? ownerCounts.Values.Sum()
                : 0;
        }
    }

    public int IncrementUnread(string? principalId, string? handle)
    {
        if (string.IsNullOrWhiteSpace(principalId) || string.IsNullOrWhiteSpace(handle))
        {
            return 0;
        }

        lock (syncRoot)
        {
            var ownerCounts = GetOrCreateUnreadCounts(principalId);
            ownerCounts[handle] = ownerCounts.TryGetValue(handle, out var count) ? count + 1 : 1;
            return ownerCounts[handle];
        }
    }

    public bool ClearUnread(string? principalId, string? handle)
    {
        if (string.IsNullOrWhiteSpace(principalId) || string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        lock (syncRoot)
        {
            return unreadCounts.TryGetValue(principalId, out var ownerCounts)
                   && ownerCounts.Remove(handle);
        }
    }

    public IReadOnlyList<string> ClearAllUnread(string? principalId)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return [];
        }

        lock (syncRoot)
        {
            if (!unreadCounts.TryGetValue(principalId, out var ownerCounts) || ownerCounts.Count == 0)
            {
                return [];
            }

            var handles = ownerCounts.Keys.ToList();
            ownerCounts.Clear();
            return handles;
        }
    }

    private List<SurfaceTimelineItem> GetOrCreateTimeline(string principalId)
    {
        if (!timelines.TryGetValue(principalId, out var timeline))
        {
            timeline = [];
            timelines[principalId] = timeline;
        }

        return timeline;
    }

    private HashSet<string> GetOrCreateDedupeKeys(string principalId)
    {
        if (!dedupeKeys.TryGetValue(principalId, out var keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            dedupeKeys[principalId] = keys;
        }

        return keys;
    }

    private Dictionary<string, int> GetOrCreateUnreadCounts(string principalId)
    {
        if (!unreadCounts.TryGetValue(principalId, out var ownerCounts))
        {
            ownerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            unreadCounts[principalId] = ownerCounts;
        }

        return ownerCounts;
    }

    private static void Trim(
        List<SurfaceTimelineItem> timeline,
        HashSet<string> keys,
        int maxTimelineItems)
    {
        var max = Math.Max(1, maxTimelineItems);
        while (timeline.Count > max)
        {
            var removed = timeline[0];
            timeline.RemoveAt(0);
            if (BuildDedupeKey(removed) is { } removedKey)
            {
                keys.Remove(removedKey);
            }
        }
    }

    private static string? BuildDedupeKey(SurfaceTimelineItem item)
    {
        var messageId = item.SourceMessage?.Id;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        return $"{item.Kind}:{messageId}";
    }
}
