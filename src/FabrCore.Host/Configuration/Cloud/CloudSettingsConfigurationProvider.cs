using Microsoft.Extensions.Configuration;

namespace FabrCore.Host.Configuration.Cloud;

/// <summary>
/// Holds the flat <c>settings</c> map delivered by a cloud server as a live
/// <see cref="IConfiguration"/> layer.
/// <para>
/// <see cref="Apply"/> replaces the whole layer and raises the provider's change token, which is
/// what causes <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> consumers to
/// re-bind. Consumers that take <c>IOptions&lt;T&gt;</c> captured their value at startup and keep
/// it until the process restarts — see <see cref="FabrCoreSettingsCatalog"/>.
/// </para>
/// </summary>
internal sealed class CloudSettingsConfigurationProvider : ConfigurationProvider
{
    private readonly object gate = new();

    /// <summary>Keys currently applied by this provider.</summary>
    public IReadOnlyCollection<string> AppliedKeys
    {
        get
        {
            lock (gate)
            {
                return Data.Keys.ToArray();
            }
        }
    }

    /// <summary>A snapshot of the currently applied key/value pairs.</summary>
    public Dictionary<string, string?> Snapshot()
    {
        lock (gate)
        {
            return new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Keys refused by <see cref="CloudSettingsPolicy"/> on the most recent apply.</summary>
    public IReadOnlyList<CloudSettingRejectionRecord> Rejected { get; private set; } = [];

    /// <summary>
    /// Replaces the cloud settings layer and notifies configuration consumers. Returns the
    /// filter result so callers can log counts — values are never logged, since connection
    /// strings and provider keys flow through here.
    /// </summary>
    public CloudSettingsFilterResult Apply(IDictionary<string, string?>? settings)
    {
        var filtered = CloudSettingsPolicy.Filter(settings);
        lock (gate)
        {
            Data = new Dictionary<string, string?>(filtered.Accepted, StringComparer.OrdinalIgnoreCase);
            Rejected = filtered.Rejected;
        }

        OnReload();
        return filtered;
    }
}

/// <summary>
/// Configuration source wrapping a single long-lived <see cref="CloudSettingsConfigurationProvider"/>.
/// The provider instance is created before the source is registered so the bootstrap fetch and
/// later background refreshes push into the same layer.
/// </summary>
internal sealed class CloudSettingsConfigurationSource(CloudSettingsConfigurationProvider provider)
    : IConfigurationSource
{
    public CloudSettingsConfigurationProvider Provider { get; } = provider;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}
