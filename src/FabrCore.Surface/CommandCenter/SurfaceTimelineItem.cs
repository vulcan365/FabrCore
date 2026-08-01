using FabrCore.Core;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.CommandCenter;

public enum SurfaceTimelineItemKind
{
    Principal,
    Agent,
    Status,
    Error,
    AdaptiveCard
}

public sealed class SurfaceTimelineItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? AgentHandle { get; set; }

    public SurfaceTimelineItemKind Kind { get; set; }

    public bool IsSystemMessage { get; set; }

    public bool DisplayInChat { get; set; } = true;

    public string? Author { get; set; }

    public string? Text { get; set; }

    public string? MessageType { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public AgentMessage? SourceMessage { get; set; }

    public AdaptiveCardSurfaceEnvelope? Envelope { get; set; }
}
