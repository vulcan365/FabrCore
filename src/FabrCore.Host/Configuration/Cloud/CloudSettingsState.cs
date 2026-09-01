using FabrCore.Core.CloudServer;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Configuration.Cloud;

/// <summary>
/// Tracks the cloud settings layer for the lifetime of the process: the live configuration
/// provider, the values that were in effect when the process started, and which cloud-delivered
/// changes are therefore still waiting on a restart.
/// <para>
/// The baseline matters because most host options are consumed through <c>IOptions&lt;T&gt;</c>
/// and captured once at startup. When a later refresh changes such a key, the running process
/// keeps using the startup value — so the host reports the key as pending rather than pretending
/// the change took effect.
/// </para>
/// </summary>
internal sealed class CloudSettingsState
{
    private readonly object gate = new();
    private readonly Dictionary<string, string?> baseline;
    private CloudConfigurationEnvelope? bootstrapEnvelope;

    public CloudSettingsState(
        CloudSettingsConfigurationProvider provider,
        CloudConfigurationEnvelope? bootstrapEnvelope)
    {
        Provider = provider;
        this.bootstrapEnvelope = bootstrapEnvelope;
        baseline = provider.Snapshot();
        AppliedSettingsVersion = bootstrapEnvelope?.ConfigurationVersion;
    }

    /// <summary>The live cloud settings configuration layer.</summary>
    public CloudSettingsConfigurationProvider Provider { get; }

    /// <summary>The configuration version whose settings are currently applied, if any.</summary>
    public string? AppliedSettingsVersion { get; private set; }

    /// <summary>
    /// Keys whose cloud value now differs from the value this process started with, and whose
    /// consumers cannot pick up a change without a restart. Ordered for stable reporting.
    /// </summary>
    public IReadOnlyList<string> PendingRestartSettings { get; private set; } = [];

    /// <summary>
    /// Hands the bootstrap-fetched envelope to the first caller and clears it, so the background
    /// sync service can adopt the snapshot already fetched at builder time instead of issuing a
    /// second full fetch during startup.
    /// </summary>
    public CloudConfigurationEnvelope? TakeBootstrapEnvelope()
    {
        lock (gate)
        {
            var envelope = bootstrapEnvelope;
            bootstrapEnvelope = null;
            return envelope;
        }
    }

    /// <summary>
    /// Applies an envelope's settings map to the live configuration layer and recomputes the
    /// pending-restart set. Logs counts only — values are never logged, because connection
    /// strings and provider secrets flow through this map.
    /// </summary>
    public void Apply(CloudConfigurationEnvelope envelope, FabrCoreSettingsCatalog catalog, ILogger logger)
    {
        var filtered = Provider.Apply(envelope.Settings);

        lock (gate)
        {
            AppliedSettingsVersion = envelope.ConfigurationVersion;
            PendingRestartSettings = ComputePendingRestart(filtered.Accepted, catalog);
        }

        LogSummary(filtered, PendingRestartSettings, envelope.ConfigurationVersion, logger);
    }

    private List<string> ComputePendingRestart(
        Dictionary<string, string?> applied,
        FabrCoreSettingsCatalog catalog)
    {
        var pending = new List<string>();
        var keys = new HashSet<string>(applied.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(baseline.Keys);

        foreach (var key in keys)
        {
            if (catalog.GetApplyMode(key) != SettingsApplyMode.RestartRequired)
            {
                continue;
            }

            baseline.TryGetValue(key, out var startupValue);
            applied.TryGetValue(key, out var currentValue);
            if (!string.Equals(startupValue, currentValue, StringComparison.Ordinal))
            {
                pending.Add(key);
            }
        }

        pending.Sort(StringComparer.OrdinalIgnoreCase);
        return pending;
    }

    /// <summary>
    /// Writes the one-line summary an operator needs to confirm what a publish actually did.
    /// Rejected keys are named because a silently dropped key is the worst failure mode here —
    /// values are never included.
    /// </summary>
    public static void LogSummary(
        CloudSettingsFilterResult filtered,
        IReadOnlyList<string> pendingRestart,
        string configurationVersion,
        ILogger logger)
    {
        logger.LogInformation(
            "Cloud settings version {Version}: {AppliedCount} applied, {PendingCount} pending restart, " +
            "{RejectedCount} rejected",
            configurationVersion,
            filtered.Accepted.Count,
            pendingRestart.Count,
            filtered.Rejected.Count);

        if (pendingRestart.Count > 0)
        {
            logger.LogWarning(
                "Cloud settings requiring a host restart before they take effect: {Keys}",
                string.Join(", ", pendingRestart));
        }

        foreach (var group in filtered.Rejected.GroupBy(rejection => rejection.Reason))
        {
            var keys = string.Join(", ", group.Select(rejection => rejection.Key));
            switch (group.Key)
            {
                case CloudSettingRejection.Blocked:
                    logger.LogWarning(
                        "Cloud settings REJECTED — these keys can never be set remotely because they own " +
                        "host enrollment and the remote recovery path: {Keys}", keys);
                    break;
                case CloudSettingRejection.Malformed:
                    logger.LogWarning("Cloud settings REJECTED — malformed configuration keys: {Keys}", keys);
                    break;
                default:
                    logger.LogWarning(
                        "Cloud settings REJECTED — payload exceeded the {MaxKeys} key / {MaxLength} character " +
                        "bound: {Keys}",
                        CloudSettingsPolicy.MaxKeyCount, CloudSettingsPolicy.MaxTotalValueLength, keys);
                    break;
            }
        }
    }
}
