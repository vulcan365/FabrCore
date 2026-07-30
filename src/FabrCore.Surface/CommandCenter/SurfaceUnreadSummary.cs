namespace FabrCore.Surface.CommandCenter;

public sealed record SurfaceUnreadSummary(
    string Handle,
    string DisplayName,
    int UnreadCount,
    bool IsSquad);
