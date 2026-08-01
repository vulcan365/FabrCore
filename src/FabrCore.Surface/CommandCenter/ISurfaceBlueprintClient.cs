namespace FabrCore.Surface.CommandCenter;

public interface ISurfaceBlueprintClient
{
    Task<SurfaceBlueprintDocument?> GetAsync(
        string principalId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string principalId,
        SurfaceBlueprintDocument blueprint,
        CancellationToken cancellationToken = default);

    Task<SurfaceBlueprintApplyResult> ApplyAsync(
        string principalId,
        SurfaceBlueprintDocument blueprint,
        CancellationToken cancellationToken = default);
}
