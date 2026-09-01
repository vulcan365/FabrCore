using FabrCore.Core.CloudServer;
using FabrCore.Host.Services.CloudServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Configuration.Cloud;

/// <summary>
/// Fetches cloud configuration at builder time and layers its <c>settings</c> map into
/// <see cref="IConfiguration"/> before anything reads it.
/// <para>
/// This has to happen during host construction rather than in the background sync service,
/// because the settings that matter most for provisioning — Orleans clustering, connection
/// strings — are read while the silo is being built and never again. Blocking here is the same
/// trade-off made by other central-configuration providers, and it is bounded by the configured
/// request timeout and a small fixed retry count.
/// </para>
/// </summary>
internal static class CloudSettingsBootstrapper
{
    // One attempt only. This runs inside host construction, so it must stay bounded and
    // predictable; CloudServerSyncService already owns robust retry, backoff and the
    // StartupFailureBehavior decision. A miss here is visible rather than silent: the sync
    // service applies the settings moments later and every key that needed a restart is
    // reported as pending.
    private const int FetchAttempts = 1;

    /// <summary>
    /// Applies cloud settings to <paramref name="builder"/> and returns the resulting state, or
    /// null when the feature is off — in which case nothing about the host changes.
    /// </summary>
    public static CloudSettingsState? TryApply(WebApplicationBuilder builder, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("FabrCore.Host.CloudSettings");

        // Read enrollment from local configuration only. This is deliberate and is why the
        // enrollment section is on the permanent blocklist: a host must always be able to
        // determine which server it belongs to without consulting that server.
        var options = builder.Configuration
            .GetSection(CloudServerOptions.SectionName)
            .Get<CloudServerOptions>() ?? new CloudServerOptions();

        if (!options.Enabled || !options.Settings.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            // CloudServerOptionsValidator reports this properly at startup. Making a request
            // that is certain to be rejected would only add a confusing earlier error.
            logger.LogDebug("Cloud settings bootstrap skipped — no Cloud Server API key is configured.");
            return null;
        }

        var envelope = Fetch(builder, options, loggerFactory, logger);

        var provider = new CloudSettingsConfigurationProvider();
        var filtered = provider.Apply(envelope?.Settings);
        Insert(builder.Configuration, new CloudSettingsConfigurationSource(provider), logger);

        // Nothing can be pending at bootstrap: what was just applied is what this process starts
        // with, so the baseline and the applied set are identical by construction.
        CloudSettingsState.LogSummary(
            filtered, [], envelope?.ConfigurationVersion ?? "(none)", logger);

        return new CloudSettingsState(provider, envelope);
    }

    private static CloudConfigurationEnvelope? Fetch(
        WebApplicationBuilder builder,
        CloudServerOptions options,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        var optionsWrapper = Options.Create(options);
        using var connectClient = new CloudServerConnectClient();
        var apiClient = new CloudServerApiClient(
            new BootstrapHttpClientFactory(),
            connectClient,
            optionsWrapper,
            Options.Create(new RemoteAdministrationOptions()),
            builder.Configuration,
            builder.Environment,
            loggerFactory.CreateLogger<CloudServerApiClient>());
        var diskCache = new CloudConfigurationDiskCache(
            optionsWrapper, builder.Environment, loggerFactory.CreateLogger<CloudConfigurationDiskCache>());

        for (var attempt = 1; attempt <= FetchAttempts; attempt++)
        {
            var result = apiClient.FetchConfigurationAsync(currentVersion: null).GetAwaiter().GetResult();
            if (result.Status == CloudConfigurationFetchStatus.Success)
            {
                diskCache.WriteAsync(result.Envelope!).GetAwaiter().GetResult();
                return result.Envelope;
            }

            logger.LogWarning(
                "Cloud settings bootstrap fetch attempt {Attempt}/{Attempts} failed: {Error}",
                attempt, FetchAttempts, result.Error);
        }

        var cached = diskCache.TryReadAsync().GetAwaiter().GetResult();
        if (cached is not null)
        {
            logger.LogWarning(
                "Cloud server unreachable during bootstrap — applying settings from cached configuration " +
                "version {Version} issued {IssuedAt:u}.",
                cached.ConfigurationVersion, cached.IssuedAt);
            return cached;
        }

        // Deliberately does not throw. CloudServerSyncService owns StartupFailureBehavior and
        // will apply it moments later; failing here as well would move a long-established
        // startup error into host construction and give it two owners.
        logger.LogWarning(
            "Cloud settings bootstrap could not reach {Url} and no cache exists at {CachePath} — " +
            "continuing with local configuration. Settings delivered by the background sync will apply " +
            "to consumers that can observe changes, and any that cannot will be reported as pending restart.",
            options.Url, diskCache.CacheFilePath);
        return null;
    }

    /// <summary>
    /// Inserts the cloud layer ahead of the environment-variable and command-line sources.
    /// <para>
    /// The resulting precedence is appsettings &lt; cloud &lt; environment. Central configuration
    /// therefore wins over a stale file on the box, while an operator retains a local override
    /// that does not depend on reaching the console — which matters when the thing being fixed is
    /// a bad publish.
    /// </para>
    /// </summary>
    internal static void Insert(
        IConfigurationBuilder configuration,
        CloudSettingsConfigurationSource source,
        ILogger logger)
    {
        // Scan backwards, not forwards. A WebApplicationBuilder carries environment-variable
        // sources on BOTH sides of the appsettings files: the DOTNET_/ASPNETCORE_ prefixed host
        // sources come first, and the unprefixed application source comes last. Inserting before
        // the first match would bury the cloud layer underneath appsettings.json, silently
        // inverting the intended precedence. What we want is the position immediately before the
        // trailing run of environment and command-line sources.
        var sources = configuration.Sources;
        var index = sources.Count;
        while (index > 0 &&
               sources[index - 1] is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource)
        {
            index--;
        }

        sources.Insert(index, source);
        logger.LogDebug(
            "Cloud settings configuration layer inserted at index {Index} of {SourceCount}: [{Order}]",
            index,
            sources.Count,
            string.Join(" < ", sources.Select(s => s.GetType().Name)));
    }

    /// <summary>
    /// Minimal factory for the bootstrap fetch. The DI container does not exist yet, and the one
    /// request made here needs no shared handler pooling.
    /// </summary>
    private sealed class BootstrapHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
