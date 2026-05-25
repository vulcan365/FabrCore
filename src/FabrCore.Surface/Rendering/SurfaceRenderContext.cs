using FabrCore.Core;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Rendering;

public sealed class SurfaceRenderContext
{
    public required AdaptiveCardSurfaceEnvelope Envelope { get; init; }

    public required ISurfaceClientContext ClientContext { get; init; }

    public AgentMessage? SourceMessage { get; init; }
}
