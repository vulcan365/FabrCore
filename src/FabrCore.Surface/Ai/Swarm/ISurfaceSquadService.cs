namespace FabrCore.Surface.Ai.Swarm;

public interface ISurfaceSquadService
{
    Task<SurfaceSwarmSquadCreateResult> CreateSquadAsync(
        ISurfacePrincipalContext context,
        string principalHandle,
        SurfaceSwarmSquadDefinition definition,
        CancellationToken cancellationToken = default);

    Task<SurfaceSwarmSquadCreateResult> EnsureSquadConfiguredAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        CancellationToken cancellationToken = default);

    Task<SurfaceSwarmSquad> AddExistingAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        SurfaceSwarmSquadAgent agent,
        CancellationToken cancellationToken = default);

    Task<SurfaceSwarmSquad> RemoveAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        string agentHandle,
        CancellationToken cancellationToken = default);

    Task<SurfaceSwarmSquadCreateResult> CreateSquadAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        SurfaceSwarmSquadAgentDefinition agentDefinition,
        CancellationToken cancellationToken = default);
}
