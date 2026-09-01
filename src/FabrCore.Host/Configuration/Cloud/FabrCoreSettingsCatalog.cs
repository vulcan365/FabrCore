namespace FabrCore.Host.Configuration.Cloud;

/// <summary>Whether a setting takes effect on the running host or only after a restart.</summary>
public enum SettingsApplyMode
{
    /// <summary>Consumed through <c>IOptionsMonitor&lt;T&gt;</c> — a cloud change applies immediately.</summary>
    Live,

    /// <summary>
    /// Consumed through <c>IOptions&lt;T&gt;</c>, read once at startup, or used before the
    /// container exists (Orleans clustering, connection strings). A cloud change is stored and
    /// reported as pending, and takes effect the next time the process starts.
    /// </summary>
    RestartRequired
}

/// <summary>Describes one configuration key (or section) a cloud server may manage.</summary>
public sealed record FabrCoreSettingDescriptor(
    string Key,
    string Type,
    string? DefaultValue,
    string Description,
    SettingsApplyMode ApplyMode,
    bool IsSection = false);

/// <summary>
/// Lets an add-on describe the configuration keys it owns so they appear in the settings catalog
/// without the host hardcoding them. Register with
/// <c>services.AddSingleton&lt;IFabrCoreSettingsCatalogContributor, MyContributor&gt;()</c>.
/// </summary>
public interface IFabrCoreSettingsCatalogContributor
{
    /// <summary>Descriptors for the keys this component owns.</summary>
    IEnumerable<FabrCoreSettingDescriptor> GetSettings();
}

/// <summary>
/// The host's view of which configuration keys exist, what they do, and whether a cloud-delivered
/// change to them applies live or needs a restart. Served to management consoles through
/// <c>GET /fabrcoreapi/admin/v1/settings/catalog</c>.
/// <para>
/// Apply modes are deliberately conservative: only the handful of keys whose consumers actually
/// take <c>IOptionsMonitor&lt;T&gt;</c> are <see cref="SettingsApplyMode.Live"/>, and any key not
/// in the catalog is treated as <see cref="SettingsApplyMode.RestartRequired"/>.
/// </para>
/// </summary>
public sealed class FabrCoreSettingsCatalog
{
    private static readonly string[] SecretKeyFragments =
        ["apikey", "secret", "password", "connectionstring", "clientsecret", "token"];

    private readonly List<FabrCoreSettingDescriptor> descriptors;

    public FabrCoreSettingsCatalog(IEnumerable<IFabrCoreSettingsCatalogContributor>? contributors = null)
    {
        descriptors = [.. BuiltIn];
        if (contributors is not null)
        {
            foreach (var contributor in contributors)
            {
                descriptors.AddRange(contributor.GetSettings());
            }
        }

        // Longest key first so lookups resolve most-specific-wins.
        descriptors.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));
    }

    /// <summary>All known descriptors, most specific key first.</summary>
    public IReadOnlyList<FabrCoreSettingDescriptor> Descriptors => descriptors;

    /// <summary>
    /// Resolves the apply mode for a key by most-specific match. Unknown keys are
    /// <see cref="SettingsApplyMode.RestartRequired"/> — claiming a key applies live when it does
    /// not would mislead an operator into thinking a change had taken effect.
    /// </summary>
    public SettingsApplyMode GetApplyMode(string key)
    {
        foreach (var descriptor in descriptors)
        {
            if (Matches(descriptor, key))
            {
                return descriptor.ApplyMode;
            }
        }

        return SettingsApplyMode.RestartRequired;
    }

    /// <summary>The descriptor matching this key, if the catalog knows it.</summary>
    public FabrCoreSettingDescriptor? Find(string key) =>
        descriptors.FirstOrDefault(descriptor => Matches(descriptor, key));

    private static bool Matches(FabrCoreSettingDescriptor descriptor, string key) =>
        key.Equals(descriptor.Key, StringComparison.OrdinalIgnoreCase) ||
        (descriptor.IsSection && key.StartsWith(descriptor.Key + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a key's value must never leave the host. Catalog responses report set/not-set for
    /// these rather than the value itself.
    /// </summary>
    public static bool IsSecret(string key)
    {
        if (key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var fragment in SecretKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly FabrCoreSettingDescriptor[] BuiltIn =
    [
        // --- Live: the only consumers that take IOptionsMonitor<T>. ---
        new("FabrCore:Host:GatewayDiscovery", "section", null,
            "Orleans gateway advertisement and refresh. Read through IOptionsMonitor, so changes apply live.",
            SettingsApplyMode.Live, IsSection: true),
        new("FabrCore:AdminAuthentication", "section", null,
            "Administration API key authentication. Read through IOptionsMonitor, so changes apply live.",
            SettingsApplyMode.Live, IsSection: true),

        // --- Bootstrap infrastructure: read before the container exists. ---
        new("FabrCore:Orleans", "section", null,
            "Orleans clustering: ClusterId, ServiceId, ClusteringMode, connection strings. Read before the silo is built.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("ConnectionStrings", "section", null,
            "Backing store connection strings (MemoryDb, GraphRagDb). Read at service registration.",
            SettingsApplyMode.RestartRequired, IsSection: true),

        // --- Behavioural, but consumed through IOptions<T> and frozen at startup. ---
        new("FabrCore:Host", "section", null,
            "Host limits: MaxIncomingMessageBytes, OutboundQueueCapacity, WebSocketPath, keep-alive.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:WebSocket", "section", null,
            "WebSocket ticket lifetime, shard counts, delivery retention and per-principal limits.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:Acl", "section", null,
            "Access control: enforcement mode, system principal, cache TTL and seed data.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:Audit", "section", null,
            "Security audit level, categories and buffer size.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:FileStorage", "section", null,
            "File storage TTL and cleanup interval.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:AgentGrain", "section", null,
            "Agent grain heartbeat interval and latency reservoir capacity.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:PrincipalGrain", "section", null,
            "Principal grain pending-message maximum age.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:PrincipalContext", "section", null,
            "Principal context entry, key and value size limits.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:PrincipalDelivery", "section", null,
            "Delivery lease duration, recovery reminders, dead-letter retention and limits.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:ModelPricing", "section", null,
            "Per-model token pricing, parsed once by the token cost calculator.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("FabrCore:EmitAttributionHeaders", "bool", "false",
            "Stamp outbound LLM requests with agent attribution headers.",
            SettingsApplyMode.RestartRequired),
        new("Logging", "section", null,
            "Standard .NET logging levels.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("GraphRag", "section", null,
            "GraphRAG knowledge ingestion tuning.",
            SettingsApplyMode.RestartRequired, IsSection: true),

        // A2A registers IOptions<T> via Options.Create AND IOptionsMonitor<T> via Configure<T>,
        // so the same type yields different values depending on the abstraction injected. Only
        // the API-key authentication handler observes the monitor; everything else is frozen,
        // so the section is reported conservatively.
        new("A2A", "section", null,
            "A2A channel configuration. Only the API-key authentication handler observes changes live; " +
            "all other A2A consumers hold a startup snapshot and need a restart.",
            SettingsApplyMode.RestartRequired, IsSection: true),
        new("Microsoft365Copilot", "section", null,
            "Microsoft 365 Copilot channel configuration. No configuration binder is registered for this " +
            "section, so values are frozen at startup.",
            SettingsApplyMode.RestartRequired, IsSection: true)
    ];
}
