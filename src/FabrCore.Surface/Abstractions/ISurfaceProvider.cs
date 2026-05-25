using FabrCore.Sdk;
using Microsoft.Agents.AI;

namespace FabrCore.Surface.Abstractions;

/// <summary>
/// Factory for Surface services scoped to a FabrCore agent handle.
/// </summary>
public interface ISurfaceProvider
{
    Task<ISurfaceService> GetSurfaceServiceAsync(
        IFabrCoreAgentHost agentHost,
        string agentHandle,
        string? configName,
        AIAgent? planningAgent = null,
        SurfaceAgentFactory? agentFactory = null,
        CancellationToken cancellationToken = default);
}
