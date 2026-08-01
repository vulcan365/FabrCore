using FabrCore.Core.CloudServer;
using FabrCore.Core.Blueprints;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.Memory.Administration;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
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
    internal const string LocalAdminHttpClientName = "FabrCore.CloudServer.LocalAdmin";
    private const int StartupFetchAttempts = 3;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRefreshBackoff = TimeSpan.FromMinutes(30);

    private readonly CloudServerApiClient apiClient;
    private readonly CloudServerConfigurationStore store;
    private readonly CloudConfigurationDiskCache diskCache;
    private readonly CloudServerOptions options;
    private readonly IServiceProvider serviceProvider;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<CloudServerSyncService> logger;
    private readonly string hostInstanceId;
    private readonly string hostVersion;

    public CloudServerSyncService(
        CloudServerApiClient apiClient,
        CloudServerConfigurationStore store,
        CloudConfigurationDiskCache diskCache,
        IOptions<CloudServerOptions> options,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<CloudServerSyncService> logger)
    {
        this.apiClient = apiClient;
        this.store = store;
        this.diskCache = diskCache;
        this.options = options.Value;
        this.serviceProvider = serviceProvider;
        this.httpClientFactory = httpClientFactory;
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
                await ApplySnapshotAsync(result.Envelope!, cancellationToken);
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
                await ApplySnapshotAsync(cached, cancellationToken);
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
        if (options.Connect.Enabled)
        {
            loops.Add(RunConnectLoopAsync(stoppingToken));
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
                await ApplySnapshotAsync(result.Envelope!, cancellationToken);
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
        Capabilities = BuildCapabilities(),
        Timestamp = DateTimeOffset.UtcNow
    };

    private Dictionary<string, string> BuildCapabilities()
    {
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = hostVersion,
            ["host.admin"] = "1",
            ["host.admin.scope"] = "cluster",
            ["host.admin.maxBodyBytes"] = options.Connect.MaxBodyBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["host.admin.features"] = "runtime,blueprints,acl,audit,monitor,evidence"
        };

        if (serviceProvider.GetService<IMemoryAdminService>() is not null)
        {
            capabilities["memory.admin"] = MemoryAdminCapability.CurrentApiVersion;
        }

        if (serviceProvider.GetService<IGraphRagAdminService>() is not null)
        {
            capabilities["graphrag.admin"] = GraphRagAdminCapability.CurrentApiVersion;
        }

        foreach (var expander in serviceProvider.GetServices<IBlueprintExpander>())
        {
            capabilities[$"blueprint.{expander.ExtensionKey}"] = "1";
        }

        if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                string.Equals(assembly.GetName().Name, "FabrCore.Surface", StringComparison.Ordinal)))
        {
            capabilities["surface.admin"] = "1";
            capabilities["surface.admin.scope"] = "cluster";
        }

        return capabilities;
    }

    private async Task ApplySnapshotAsync(
        CloudConfigurationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        store.ApplySnapshot(envelope);

        // Blueprints is optional in the v1 protocol; third-party servers may send null even
        // though the envelope setter normalizes it. Never let an absent list block config.
        var blueprints = envelope.Blueprints ?? [];
        if (blueprints.Count == 0)
        {
            return;
        }

        var blueprintService =
            serviceProvider.GetRequiredService<FabrCore.Host.Services.IFabrCoreBlueprintService>();
        foreach (var deployment in blueprints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(deployment.PrincipalId) ||
                string.IsNullOrWhiteSpace(deployment.Blueprint?.Name))
            {
                logger.LogWarning(
                    "Skipping cloud blueprint deployment with a missing principal id or blueprint name");
                continue;
            }

            try
            {
                await blueprintService.SaveAsync(
                    deployment.PrincipalId,
                    deployment.Blueprint,
                    cancellationToken);
                if (deployment.ApplyOnRefresh)
                {
                    await blueprintService.ApplyAsync(
                        deployment.PrincipalId,
                        deployment.Blueprint,
                        cancellationToken: cancellationToken);
                }

                logger.LogInformation(
                    "Cloud blueprint {Blueprint} version {Version} stored for {Principal} (apply={Apply})",
                    deployment.Blueprint.Name,
                    deployment.Blueprint.Version,
                    deployment.PrincipalId,
                    deployment.ApplyOnRefresh);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Cloud blueprint {Blueprint} failed for principal {Principal}; other configuration remains active",
                    deployment.Blueprint?.Name,
                    deployment.PrincipalId);
            }
        }
    }

    private async Task RunConnectLoopAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Cloud Server outbound admin connect channel enabled for admin target {LocalAdminUrl}",
            options.Connect.LocalAdminUrl);

        if (!Uri.TryCreate(options.Connect.LocalAdminUrl, UriKind.Absolute, out var adminUri) ||
            !adminUri.IsLoopback)
        {
            logger.LogWarning(
                "Connect LocalAdminUrl '{LocalAdminUrl}' is not a loopback address. Admin commands and the " +
                "local admin key will traverse the network — ensure this target (e.g. a container network " +
                "alias) is trusted.",
                options.Connect.LocalAdminUrl);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var command = await apiClient.PollAdminCommandAsync(hostInstanceId, stoppingToken);
                if (command is null)
                {
                    continue;
                }

                var response = await DispatchAdminCommandAsync(command, stoppingToken);
                await apiClient.SendAdminCommandResponseAsync(response, hostInstanceId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cloud Server connect-channel iteration failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<CloudAdminCommandResponse> DispatchAdminCommandAsync(
        CloudAdminCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            return Failed(command.CommandId, 400, "Connect-channel command id is required.");
        }

        if (command.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Failed(command.CommandId, 408, "Connect-channel command expired before execution.");
        }

        if (!string.IsNullOrWhiteSpace(command.TargetHostInstanceId) &&
            !string.Equals(command.TargetHostInstanceId, hostInstanceId, StringComparison.Ordinal))
        {
            return Failed(command.CommandId, 409, "Connect-channel command was leased to another host instance.", command.LeaseToken);
        }

        if (string.IsNullOrWhiteSpace(command.LeaseToken))
        {
            return Failed(command.CommandId, 400, "Connect-channel lease token is required.");
        }

        if (!IsAllowedAdminPath(command.PathAndQuery) ||
            command.PathAndQuery.Contains('\\') ||
            command.PathAndQuery.StartsWith("//", StringComparison.Ordinal))
        {
            return Failed(command.CommandId, 403, "The requested local path is not on the administration allowlist.", command.LeaseToken);
        }

        if (command.Body?.Length > options.Connect.MaxBodyBytes)
        {
            return Failed(command.CommandId, 413, "Connect-channel request body exceeds the configured limit.");
        }

        HttpMethod method;
        try
        {
            method = new HttpMethod(command.Method);
        }
        catch (FormatException)
        {
            return Failed(command.CommandId, 400, "Connect-channel HTTP method is invalid.");
        }

        if (method != HttpMethod.Get && method != HttpMethod.Post && method != HttpMethod.Put &&
            method != HttpMethod.Patch && method != HttpMethod.Delete)
        {
            return Failed(command.CommandId, 405, $"HTTP method {method} is not allowed.");
        }

        try
        {
            var target = new Uri(
                $"{options.Connect.LocalAdminUrl.TrimEnd('/')}{command.PathAndQuery}",
                UriKind.Absolute);
            using var request = new HttpRequestMessage(method, target);
            if (command.Body is not null)
            {
                request.Content = new ByteArrayContent(command.Body);
            }

            var isVersionedAdminPath = command.PathAndQuery.StartsWith(
                "/fabrcoreapi/admin/v1", StringComparison.OrdinalIgnoreCase) ||
                command.PathAndQuery.StartsWith(
                    "/fabrcoreapi/surface/admin/v1", StringComparison.OrdinalIgnoreCase);

            foreach (var (name, values) in command.Headers)
            {
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("X-FabrCore-Admin-Target", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                    request.Content is not null)
                {
                    request.Content.Headers.TryAddWithoutValidation(name, values);
                }
                else if (name.Equals("Accept", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("If-Match", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) ||
                         (name.Equals("x-user-handle", StringComparison.OrdinalIgnoreCase) && !isVersionedAdminPath) ||
                         (name.Equals("X-FabrCore-Admin-Actor", StringComparison.OrdinalIgnoreCase) && isVersionedAdminPath))
                {
                    request.Headers.TryAddWithoutValidation(name, values);
                }
            }

            request.Headers.TryAddWithoutValidation("X-FabrCore-Admin-Command-Id", command.CommandId);

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", options.Connect.LocalAdminApiKey);
            var client = httpClientFactory.CreateClient(LocalAdminHttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await ReadLimitedAsync(
                response.Content,
                options.Connect.MaxBodyBytes,
                cancellationToken);

            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (response.Content.Headers.ContentType is not null)
            {
                headers["Content-Type"] = [response.Content.Headers.ContentType.ToString()];
            }
            if (response.Headers.ETag is not null)
            {
                headers["ETag"] = [response.Headers.ETag.ToString()];
            }

            return new CloudAdminCommandResponse
            {
                CommandId = command.CommandId,
                StatusCode = (int)response.StatusCode,
                Headers = headers,
                Body = body,
                LeaseToken = command.LeaseToken
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to execute connect-channel command {CommandId}", command.CommandId);
            return Failed(command.CommandId, 502, "The cluster could not execute the local admin request.", command.LeaseToken);
        }
    }

    private static bool IsAllowedAdminPath(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery)) return false;

        var path = pathAndQuery.Split('?', 2)[0];
        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (path.StartsWith("/fabrcoreapi/agent/chat/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/fabrcoreapi/agent/event/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] allowedPrefixes =
        [
            "/fabrcoreapi/admin/v1",
            "/fabrcoreapi/surface/admin/v1",
            "/fabrcoreapi/memory/admin/v1",
            "/fabrcoreapi/graphrag/admin/v1",
            // Compatibility paths for the existing Surface Admin integration.
            "/fabrcoreapi/capabilities",
            "/fabrcoreapi/diagnostics",
            "/fabrcoreapi/acl",
            "/fabrcoreapi/audit",
            "/fabrcoreapi/agent",
            "/fabrcoreapi/blueprint",
            "/fabrcoreapi/monitor",
            "/fabrcoreapi/verifiableexecution",
            "/fabrcoreapi/discovery",
            "/fabrcoreapi/storage/surface/"
        ];

        return allowedPrefixes.Any(prefix =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(prefix.EndsWith('/') ? prefix : $"{prefix}/", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException("Connect-channel response body exceeds the configured limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return target.ToArray();
            }
            if (target.Length + read > maxBytes)
            {
                throw new InvalidOperationException("Connect-channel response body exceeds the configured limit.");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static CloudAdminCommandResponse Failed(
        string commandId,
        int statusCode,
        string error,
        string? leaseToken = null) =>
        new()
        {
            CommandId = commandId,
            StatusCode = statusCode,
            Error = error,
            LeaseToken = leaseToken ?? string.Empty
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
