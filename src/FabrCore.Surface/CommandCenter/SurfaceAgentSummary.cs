using FabrCore.Core;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceAgentSummary
{
    public string Handle { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public bool IsShared { get; set; }

    public bool IsHidden { get; set; }

    public bool IsSurfaceAgent { get; set; }

    public AgentHealthStatus? Health { get; set; }

    public bool IsWorking { get; set; }

    public int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    public string? StatusText { get; set; }

    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    public HealthState State => Health?.State ?? HealthState.NotConfigured;
}
