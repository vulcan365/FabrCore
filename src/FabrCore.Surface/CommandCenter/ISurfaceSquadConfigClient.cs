using FabrCore.Surface.Ai.Swarm;

namespace FabrCore.Surface.CommandCenter;

public interface ISurfaceSquadConfigClient
{
    Task<IReadOnlyList<SurfaceSquad>> GetAsync(
        string principalId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string principalId,
        IReadOnlyList<SurfaceSquad> squads,
        CancellationToken cancellationToken = default);
}
