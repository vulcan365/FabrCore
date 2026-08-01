using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceBlueprintProvisioner
{
    private readonly ISurfaceBlueprintClient blueprintClient;
    private readonly ILogger<SurfaceBlueprintProvisioner> logger;

    public SurfaceBlueprintProvisioner(
        ISurfaceBlueprintClient blueprintClient,
        ILogger<SurfaceBlueprintProvisioner> logger,
        ISurfaceSquadConfigClient? squadConfigClient = null)
    {
        this.blueprintClient = blueprintClient;
        this.logger = logger;
    }

    public async Task<SurfaceBlueprintApplyResult?> ApplyStoredAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var blueprint = await blueprintClient.GetAsync(principalId, cancellationToken);
        if (blueprint is null)
        {
            return null;
        }

        return await ApplyAsync(principalId, blueprint, cancellationToken);
    }

    public async Task<SurfaceBlueprintApplyResult> ApplyAsync(
        string principalId,
        SurfaceBlueprintDocument blueprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(blueprint);

        var result = await blueprintClient.ApplyAsync(
            principalId,
            blueprint,
            cancellationToken);

        logger.LogInformation(
            "Applied FabrCore blueprint {Name} version {Version} for {PrincipalId}: {AgentCount} expanded agent configurations.",
            blueprint.Name,
            blueprint.Version,
            principalId,
            result.AgentConfigurationsRequested);

        return result;
    }
}
