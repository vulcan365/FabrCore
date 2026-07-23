using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace FabrCore.Host.Services.CloudServer;

/// <summary>
/// Pulls configuration from the cloud server and keeps it fresh.
/// <para>
/// Startup (blocking, before the host serves traffic): a few quick fetch attempts, then the
/// last-known-good disk cache, then <see cref="CloudServerOptions.StartupFailureBehavior"/>.
/// While running: a refresh loop using If-None-Match (304s are cheap) with exponential
/// backoff on failure — the last-known-good snapshot keeps serving — plus an optional
/// heartbeat loop whose response can request an immediate refresh.
/// </para>
/// </summary>
internal sealed class CloudServerSyncService : BackgroundService
{
    private const int StartupFetchAttempts = 3;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRefreshBackoff = TimeSpan.FromMinutes(30);

    private readonly CloudServerApiClient apiClient;
    private readonly CloudServerConfigurationStore store;
    private readonly CloudConfigurationDiskCache diskCache;
    private readonly CloudServerOptions options;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<CloudServerSyncService> logger;
    private readonly string hostInstanceId;
    private readonly string hostVersion;

    public CloudServerSyncService(
        CloudServerApiClient apiClient,
        CloudServerConfigurationStore store,
        CloudConfigurationDiskCache diskCache,
        IOptions<CloudServerOptions> options,
        IServiceProvider serviceProvider,
        ILogger<CloudServerSyncService> logger)
    {
        this.apiClient = apiClient;
        this.store = store;
        this.diskCache = diskCache;
        this.options = options.Value;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
        this.hostInstanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        this.hostVersion = typeof(CloudServerSyncService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Cloud Server configuration enabled — cluster '{ClusterId}', environment '{Environment}', server {Url}",
            apiClient.EffectiveClusterId, apiClient.EffectiveEnvironment, options.Url);

        var fetched = false;
        for (var attempt = 1; attempt <= StartupFetchAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            var result = await apiClient.FetchConfigurationAsync(currentVersion: null, cancellationToken);
            if (result.Status == CloudConfigurationFetchStatus.Success)
            {
                store.ApplySnapshot(result.Envelope!);
                await diskCache.WriteAsync(result.Envelope!, cancellationToken);
                fetched = true;
                break;
            }

            logger.LogWarning(
                "Cloud server configuration fetch attempt {Attempt}/{Attempts} failed: {Error}",
                attempt, StartupFetchAttempts, result.Error);

            if (attempt < StartupFetchAttempts)
            {
                await Task.Delay(StartupRetryDelay, cancellationToken);
            }
        }

        if (!fetched)
        {
            var cached = await diskCache.TryReadAsync(cancellationToken);
            if (cached is not null)
            {
                store.ApplySnapshot(cached);
                logger.LogWarning(
                    "Cloud server unreachable — running on cached configuration version {Version} issued {IssuedAt:u} " +
                    "from {Path}. Background sync will keep retrying.",
                    cached.ConfigurationVersion, cached.IssuedAt, diskCache.CacheFilePath);
            }
            else if (options.StartupFailureBehavior == CloudServerStartupFailureBehavior.Fail)
            {
                throw new InvalidOperationException(
                    $"Cloud server configuration could not be fetched from {options.Url} and no local cache exists at " +
                    $"{diskCache.CacheFilePath}. Verify {CloudServerOptions.SectionName}:Url and ApiKey, or set " +
                    $"{CloudServerOptions.SectionName}:StartupFailureBehavior to StartDegraded to start without configuration.");
            }
            else
            {
                logger.LogError(
                    "Cloud server unreachable and no cache available — starting degraded (no model configuration). " +
                    "Model and API key lookups will return 404 until the background sync succeeds.");
            }
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task> { RunRefreshLoopAsync(stoppingToken) };
        if (options.Heartbeat.Enabled)
        {
            loops.Add(RunHeartbeatLoopAsync(stoppingToken));
        }

        await Task.WhenAll(loops);
    }

    private async Task RunRefreshLoopAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetRefreshDelay(consecutiveFailures), stoppingToken);
                var refreshed = await FetchAndApplyAsync(stoppingToken);
                consecutiveFailures = refreshed ? 0 : consecutiveFailures + 1;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                logger.LogWarning(ex, "Cloud server configuration refresh iteration failed");
            }
        }
    }

    private TimeSpan GetRefreshDelay(int consecutiveFailures)
    {
        if (consecutiveFailures == 0)
        {
            return options.RefreshInterval;
        }

        var backoff = options.RefreshInterval * Math.Pow(2, Math.Min(consecutiveFailures, 6));
        return backoff > MaxRefreshBackoff ? MaxRefreshBackoff : backoff;
    }

    /// <summary>Fetches with If-None-Match and applies on change. True unless the fetch failed.</summary>
    private async Task<bool> FetchAndApplyAsync(CancellationToken cancellationToken)
    {
        var result = await apiClient.FetchConfigurationAsync(store.CurrentConfigurationVersion, cancellationToken);
        switch (result.Status)
        {
            case CloudConfigurationFetchStatus.Success:
                store.ApplySnapshot(result.Envelope!);
                await diskCache.WriteAsync(result.Envelope!, cancellationToken);
                return true;
            case CloudConfigurationFetchStatus.NotModified:
                logger.LogDebug("Cloud configuration unchanged (version {Version})", store.CurrentConfigurationVersion);
                return true;
            default:
                logger.LogWarning(
                    "Cloud configuration refresh failed — continuing on version {Version}: {Error}",
                    store.CurrentConfigurationVersion ?? "(none)", result.Error);
                return false;
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Heartbeat.Interval, stoppingToken);
                var response = await apiClient.SendHeartbeatAsync(BuildHeartbeat(), stoppingToken);
                if (response?.RefreshRequested == true)
                {
                    logger.LogInformation("Cloud server requested an immediate configuration refresh");
                    await FetchAndApplyAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cloud server heartbeat iteration failed");
            }
        }
    }

    private CloudHeartbeatRequest BuildHeartbeat() => new()
    {
        ClusterId = apiClient.EffectiveClusterId,
        Environment = apiClient.EffectiveEnvironment,
        ServiceId = apiClient.ServiceId,
        HostInstanceId = hostInstanceId,
        HostVersion = hostVersion,
        AppliedConfigurationVersion = store.CurrentConfigurationVersion,
        ActiveGatewayCount = TryGetActiveGatewayCount(),
        Timestamp = DateTimeOffset.UtcNow
    };

    private int TryGetActiveGatewayCount()
    {
        try
        {
            // Resolved lazily: the discovery source requires Orleans cluster membership,
            // which is unavailable in non-Orleans hosts and during early startup.
            var source = serviceProvider.GetService<IGatewayDiscoverySource>();
            return source?.GetGateways().Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
