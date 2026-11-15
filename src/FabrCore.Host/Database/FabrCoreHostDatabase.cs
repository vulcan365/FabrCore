using FabrCore.Host.Configuration;
using Microsoft.Extensions.Configuration;

namespace FabrCore.Host.Database;

/// <summary>
/// The database a host is given where it runs, set in its own environment (<c>FabrCore__HostDatabase__Default</c>) and checked before
/// anything a cloud server sends. One <see cref="Default"/> connection covers every FabrCore feature: Orleans membership and state, ACL,
/// Memory, GraphRAG and the operational database. Each other key moves one feature to a database of its own. A feature with neither
/// falls back to what it reads today (<c>FabrCore:Orleans:*</c> and <c>ConnectionStrings:{name}</c>, from files or a cloud server).
/// <para>
/// The section is local only: a cloud server can never set it (<c>CloudSettingsPolicy</c>), and what it settles stays above the cloud
/// layer, so a publish meant for somewhere else can't point a host at another database. Changes require restart.
/// </para>
/// </summary>
public static class FabrCoreHostDatabase
{
    public const string SectionName = "FabrCore:HostDatabase";

    /// <summary>The connection every feature uses unless it has its own.</summary>
    public const string Default = "Default";

    /// <summary>Orleans cluster membership, reminders and AdoNet streams.</summary>
    public const string Membership = "Membership";

    /// <summary>Orleans grain and pub/sub storage, which holds agents' state. Falls back to <see cref="Membership"/>, then <see cref="Default"/>.</summary>
    public const string State = "State";

    public const string Acl = "Acl";
    public const string Memory = "Memory";
    public const string GraphRag = "GraphRag";
    public const string Operations = "Operations";

    /// <summary>The connection name FabrCore's own features read a feature's override from.</summary>
    internal static string ConnectionName(string feature) => $"FabrCoreHost{feature}";

    private static readonly string[] Features = [Acl, Memory, GraphRag, Operations];
    private static readonly string[] Keys = [Default, Membership, State, .. Features];

    /// <summary>
    /// What the host's own database settles, as the keys FabrCore's readers already use, or null when the host sets none. A feature without
    /// an override is cleared when <see cref="Default"/> is set, so a name sent from elsewhere can't move it.
    /// </summary>
    internal static Dictionary<string, string?>? Settle(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            if (!Keys.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{SectionName}:{child.Key} isn't a host database setting. Use one of {string.Join(", ", Keys)}.");
            if (string.IsNullOrWhiteSpace(child.Value))
                throw new InvalidOperationException($"{SectionName}:{child.Key} is set but empty.");
            values[child.Key] = child.Value;
        }
        if (values.Count == 0)
            return null;

        var settled = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var main = values.GetValueOrDefault(Default);
        if (main is not null)
        {
            settled[$"ConnectionStrings:{new FabrCoreDatabaseOptions().ConnectionStringName}"] = main;
            settled[$"{FabrCoreDatabaseOptions.SectionName}:ConnectionStringName"] = new FabrCoreDatabaseOptions().ConnectionStringName;
        }

        foreach (var feature in Features)
        {
            var nameKey = $"{FabrCoreDatabaseOptions.SectionName}:{feature}ConnectionStringName";
            if (values.GetValueOrDefault(feature) is { } own)
            {
                settled[$"ConnectionStrings:{ConnectionName(feature)}"] = own;
                settled[nameKey] = ConnectionName(feature);
            }
            else if (main is not null)
            {
                settled[nameKey] = null;
            }
        }

        var membership = values.GetValueOrDefault(Membership) ?? main;
        var state = values.GetValueOrDefault(State) ?? membership;
        if (membership is not null)
        {
            settled[$"{OrleansClusterOptions.SectionName}:ClusteringMode"] = nameof(ClusteringMode.SqlServer);
            settled[$"{OrleansClusterOptions.SectionName}:ConnectionString"] = membership;
        }
        if (state is not null)
            settled[$"{OrleansClusterOptions.SectionName}:StorageConnectionString"] = state;
        return settled;
    }

    /// <summary>
    /// Adds what <see cref="Settle"/> gives as the host's top configuration layer, once. Cloud and runtime rule layers are inserted below it
    /// (<see cref="ConfigurationLayering.TrailingIndex"/>).
    /// </summary>
    internal static void Apply(IConfigurationBuilder builder, IConfiguration configuration)
    {
        if (builder.Sources.OfType<HostDatabaseConfigurationSource>().Any() || Settle(configuration) is not { } settled)
            return;
        builder.Sources.Add(new HostDatabaseConfigurationSource(settled));
    }
}

/// <summary>The host's own database settings, kept above every other configuration layer.</summary>
internal sealed class HostDatabaseConfigurationSource(IReadOnlyDictionary<string, string?> values) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new HostDatabaseConfigurationProvider(values);
}

internal sealed class HostDatabaseConfigurationProvider : ConfigurationProvider
{
    internal HostDatabaseConfigurationProvider(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
            Data[key] = value;
    }
}
