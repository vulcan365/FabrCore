using FabrCore.Core;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Actions;

public sealed class SurfaceActionContext
{
    public required AdaptiveCardSurfaceEnvelope Envelope { get; init; }

    public required ISurfacePrincipalContext PrincipalContext { get; init; }

    public AgentMessage? SourceMessage { get; init; }
}
