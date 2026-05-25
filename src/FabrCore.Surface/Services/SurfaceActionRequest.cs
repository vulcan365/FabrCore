using FabrCore.Core;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Services;

public sealed class SurfaceActionRequest
{
    public string ActionId { get; init; } = string.Empty;

    public string ActionType { get; init; } = string.Empty;

    public string? Verb { get; init; }

    public AdaptiveCardSurfaceEnvelope Envelope { get; init; } = new();

    public AgentMessage? SourceMessage { get; init; }

    public Dictionary<string, object?> Payload { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
