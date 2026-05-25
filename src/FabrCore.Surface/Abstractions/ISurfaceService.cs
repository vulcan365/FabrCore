using FabrCore.Core;
using FabrCore.Surface.Configuration;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Abstractions;

/// <summary>
/// Agent-side facade for producing and sending FabrCore Surface UI render messages.
/// </summary>
public interface ISurfaceService
{
    string AgentHandle { get; }

    SurfaceDefinition Definition { get; }

    Task<AdaptiveCardSurfaceEnvelope> PlanAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    Task<AgentMessage> RenderAsync(
        AdaptiveCardSurfaceEnvelope envelope,
        AgentMessage sourceMessage,
        string? targetHandle = null,
        CancellationToken cancellationToken = default);

    Task<AgentMessage> PlanAndRenderAsync(
        string prompt,
        AgentMessage sourceMessage,
        string? targetHandle = null,
        CancellationToken cancellationToken = default);
}
