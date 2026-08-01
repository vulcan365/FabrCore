using FabrCore.Core.Acl;
using FabrCore.Sdk;
using FabrCore.Surface.CommandCenter;

namespace FabrCore.SampleApp.Surface;

public sealed class SurfaceDemoBootstrapper(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<SurfaceDemoBootstrapper> logger) : BackgroundService
{
    public const string PrincipalHandle = SurfaceDemoBlueprintFactory.PrincipalHandle;

    public const string CrmAgentHandle = SurfaceDemoBlueprintFactory.CrmAgentHandle;

    public const string SurfaceAgentHandle = SurfaceDemoBlueprintFactory.SurfaceAgentHandle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            await SaveAndApplyBlueprintWithRetryAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "SurfaceApp demo agents could not be bootstrapped. The /surface page will still load, but the demo requires a valid default model configuration.");
        }
    }

    private async Task SaveAndApplyBlueprintWithRetryAsync(CancellationToken cancellationToken)
    {
        var blueprint = SurfaceDemoBlueprintFactory.Create();
        const int maxAttempts = 10;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var scope = scopeFactory.CreateScope();
                var hostApiClient = scope.ServiceProvider.GetRequiredService<IFabrCoreHostApiClient>();
                var blueprintClient = scope.ServiceProvider.GetRequiredService<ISurfaceBlueprintClient>();
                var blueprintProvisioner = scope.ServiceProvider.GetRequiredService<SurfaceBlueprintProvisioner>();

                await EnsureDemoPrincipalAsync(hostApiClient, cancellationToken);
                await blueprintClient.SaveAsync(PrincipalHandle, blueprint, cancellationToken);
                var result = await blueprintProvisioner.ApplyAsync(PrincipalHandle, blueprint, cancellationToken);

                logger.LogInformation(
                    "SurfaceApp demo blueprint {BlueprintName} version {Version} applied for principal {PrincipalHandle}: {AgentCount} agent configs, {SquadsCreated} squads created, {SquadsSkipped} squads skipped.",
                    result.Name,
                    result.Version,
                    PrincipalHandle,
                    result.AgentConfigurationsRequested,
                    result.SquadsCreated,
                    result.SquadsSkipped);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(500 * attempt);
                logger.LogWarning(
                    ex,
                    "SurfaceApp demo blueprint apply attempt {Attempt} of {MaxAttempts} failed; retrying in {Delay}.",
                    attempt,
                    maxAttempts,
                    delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static async Task EnsureDemoPrincipalAsync(
        IFabrCoreHostApiClient hostApiClient,
        CancellationToken cancellationToken)
    {
        var principal = await hostApiClient.GetAclPrincipalAsync(
                "system",
                PrincipalHandle,
                cancellationToken)
            ?? new AclPrincipal
            {
                Handle = PrincipalHandle,
                DisplayName = "Demo User"
            };

        if (!principal.Roles.Contains("acl-admin", StringComparer.OrdinalIgnoreCase))
        {
            principal.Roles.Add("acl-admin");
        }

        await hostApiClient.UpsertAclPrincipalAsync("system", principal, cancellationToken);
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            started);

        await started.Task.WaitAsync(cancellationToken);
    }
}
