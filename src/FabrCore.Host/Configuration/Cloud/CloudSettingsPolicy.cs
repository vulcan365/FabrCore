namespace FabrCore.Host.Configuration.Cloud;

/// <summary>Why a cloud-delivered setting key was not applied.</summary>
internal enum CloudSettingRejection
{
    /// <summary>The key is on the enrollment blocklist and can never be set remotely.</summary>
    Blocked,

    /// <summary>The key is not a well-formed IConfiguration key.</summary>
    Malformed,

    /// <summary>The payload exceeded the key-count or total-size bound.</summary>
    LimitExceeded
}

/// <summary>One key the policy refused, with the reason. Values are never captured.</summary>
internal sealed record CloudSettingRejectionRecord(string Key, CloudSettingRejection Reason);

/// <summary>Outcome of filtering a cloud <c>settings</c> map through the policy.</summary>
internal sealed record CloudSettingsFilterResult(
    Dictionary<string, string?> Accepted,
    IReadOnlyList<CloudSettingRejectionRecord> Rejected);

/// <summary>
/// Decides which cloud-delivered <c>settings</c> keys a host is willing to apply.
/// <para>
/// A conforming cloud server may set almost anything — Orleans clustering, connection strings,
/// ACL, timeouts — because that is what makes zero-touch provisioning possible. Three areas are
/// permanently off limits, because a bad or hostile publish there would remove the operator's
/// ability to recover the host:
/// </para>
/// <list type="bullet">
/// <item><c>FabrCore:CloudServer</c> — the enrollment block. If a server could rewrite the URL,
/// key, or Enabled flag it could orphan or redirect the entire fleet with no way back.</item>
/// <item><c>FabrCore:RemoteAdministration</c> — disabling the connect channel destroys the
/// remote recovery path.</item>
/// <item><c>FabrCore:HostUrl</c> — the connect channel dispatches local admin requests here, so
/// a remotely settable value is an SSRF pivot.</item>
/// </list>
/// </summary>
internal static class CloudSettingsPolicy
{
    /// <summary>Maximum number of keys accepted from one envelope.</summary>
    public const int MaxKeyCount = 2000;

    /// <summary>Maximum combined UTF-16 length of all accepted values.</summary>
    public const int MaxTotalValueLength = 1024 * 1024;

    private static readonly string[] BlockedSections =
    [
        CloudServerOptions.SectionName,
        RemoteAdministrationOptions.SectionName
    ];

    private static readonly string[] BlockedKeys =
    [
        FabrCore.Core.FabrCoreConfigurationKeys.HostUrl
    ];

    /// <summary>Section prefixes a cloud server may never set, for reporting to consoles.</summary>
    public static IReadOnlyList<string> BlockedSectionNames => BlockedSections;

    /// <summary>Individual keys a cloud server may never set, for reporting to consoles.</summary>
    public static IReadOnlyList<string> BlockedKeyNames => BlockedKeys;

    /// <summary>Whether the key is on the permanent enrollment blocklist.</summary>
    public static bool IsBlocked(string key)
    {
        foreach (var blocked in BlockedKeys)
        {
            if (key.Equals(blocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var section in BlockedSections)
        {
            // Segment-boundary match so "FabrCore:CloudServerExtras" is not caught by
            // the "FabrCore:CloudServer" section.
            if (key.Equals(section, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith($"{section}:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWellFormed(string key) =>
        !string.IsNullOrWhiteSpace(key) &&
        !key.Contains("..", StringComparison.Ordinal) &&
        !key.Contains("::", StringComparison.Ordinal) &&
        !key.StartsWith(':') &&
        !key.EndsWith(':');

    /// <summary>
    /// Filters a cloud settings map. Rejections are returned rather than thrown: one bad key
    /// must never cost the host an otherwise valid configuration.
    /// </summary>
    public static CloudSettingsFilterResult Filter(IDictionary<string, string?>? settings)
    {
        var accepted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<CloudSettingRejectionRecord>();
        if (settings is null || settings.Count == 0)
        {
            return new CloudSettingsFilterResult(accepted, rejected);
        }

        var totalValueLength = 0;
        foreach (var (key, value) in settings)
        {
            if (!IsWellFormed(key))
            {
                rejected.Add(new CloudSettingRejectionRecord(key ?? "(null)", CloudSettingRejection.Malformed));
                continue;
            }

            if (IsBlocked(key))
            {
                rejected.Add(new CloudSettingRejectionRecord(key, CloudSettingRejection.Blocked));
                continue;
            }

            if (accepted.Count >= MaxKeyCount ||
                totalValueLength + (value?.Length ?? 0) > MaxTotalValueLength)
            {
                rejected.Add(new CloudSettingRejectionRecord(key, CloudSettingRejection.LimitExceeded));
                continue;
            }

            totalValueLength += value?.Length ?? 0;
            accepted[key] = value;
        }

        return new CloudSettingsFilterResult(accepted, rejected);
    }
}
