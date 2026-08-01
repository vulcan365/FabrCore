using FabrCore.Surface;

namespace FabrCore.Surface.Ai.Swarm;

public interface ISurfaceBasicSquadService
{
    Task<SurfaceSquadCreateResult> CreateSquadAsync(
        ISurfacePrincipalContext context,
        string principalHandle,
        SurfaceSquadDefinition definition,
        CancellationToken cancellationToken = default);

    Task<SurfaceSquadCreateResult> EnsureSquadConfiguredAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
        CancellationToken cancellationToken = default);

    Task<SurfaceSquad> AddExistingAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
        SurfaceSquadAgent agent,
        CancellationToken cancellationToken = default);

    Task<SurfaceSquad> RemoveAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
        string agentHandle,
        CancellationToken cancellationToken = default);

    Task<SurfaceSquadCreateResult> CreateSquadAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
        SurfaceSquadAgentDefinition agentDefinition,
        CancellationToken cancellationToken = default);
}
