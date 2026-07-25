using FabrCore.Core;
using FabrCore.Core.CloudServer;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Services.CloudServer;

/// <summary>
/// Read-only <see cref="IFabrCoreConfigurationStore"/> serving an in-memory snapshot of
/// cloud-delivered configuration. The snapshot is swapped atomically by
/// <see cref="CloudServerSyncService"/>; reads never touch the network. Until a snapshot is
/// available (StartDegraded startups) an empty configuration is served, so model/key lookups
/// return 404 rather than errors.
/// </summary>
public sealed class CloudServerConfigurationStore : IFabrCoreConfigurationStore
{
    private sealed record Snapshot(FabrCoreConfiguration Configuration, string Version);

    private readonly ILogger<CloudServerConfigurationStore> logger;
    private volatile Snapshot? snapshot;
    private int emptyReadLogged;

    public CloudServerConfigurationStore(ILogger<CloudServerConfigurationStore> logger)
    {
        this.logger = logger;
    }

    public bool SupportsWrites => false;

    /// <summary>Whether a configuration snapshot has been applied.</summary>
    public bool HasSnapshot => snapshot is not null;

    /// <summary>The configuration version of the current snapshot (used for If-None-Match), if any.</summary>
    public string? CurrentConfigurationVersion => snapshot?.Version;

    public Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var current = snapshot;
        if (current is null)
        {
            if (Interlocked.Exchange(ref emptyReadLogged, 1) == 0)
            {
                logger.LogWarning(
                    "Cloud configuration requested before any snapshot is available — serving an empty " +
                    "configuration until the cloud server sync succeeds.");
            }

            return Task.FromResult(new FabrCoreConfiguration());
        }

        return Task.FromResult(current.Configuration);
    }

    public Task SaveConfigurationAsync(FabrCoreConfiguration configuration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Configuration is managed by the cloud server and is read-only on this host. " +
            "Publish changes through the cloud server instead.");

    internal void ApplySnapshot(CloudConfigurationEnvelope envelope)
    {
        snapshot = new Snapshot(envelope.Configuration, envelope.ConfigurationVersion);
        logger.LogInformation(
            "Applied cloud configuration version {Version} ({ModelCount} models, {KeyCount} API keys, issued {IssuedAt:u})",
            envelope.ConfigurationVersion,
            envelope.Configuration.ModelConfigurations.Count,
            envelope.Configuration.ApiKeys.Count,
            envelope.IssuedAt);
    }
}
