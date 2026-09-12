using FabrCore.Host.Configuration;
using Microsoft.Extensions.Configuration;

namespace FabrCore.Host.Database;

/// <summary>One database enables the complete SQL feature set. Changes require restart.</summary>
public sealed class FabrCoreDatabaseOptions
{
    public const string SectionName = "FabrCore:Database";
    public string ConnectionStringName { get; set; } = "FabrCore";
    public string? MemoryConnectionStringName { get; set; }
    public string? GraphRagConnectionStringName { get; set; }
    public string? AclConnectionStringName { get; set; }
    public string? OperationsConnectionStringName { get; set; }
    public bool AutoInitialize { get; set; } = true;
    public bool Enabled { get; private set; }
    internal string ConnectionString { get; private set; } = "";

    internal static FabrCoreDatabaseOptions Resolve(IConfiguration configuration)
    {
        if (configuration.GetSection("FabrCore:Acl:Seed").Exists() ||
            configuration.GetSection("Acl:Seed").Exists() ||
            configuration.GetSection("FabrCore:Acl:Rules").Exists())
            throw new InvalidOperationException("JSON ACL seeds/rules are no longer supported. Export/import ACL data with the migration utility, then manage it through the ACL API.");
        var result = configuration.GetSection(SectionName).Get<FabrCoreDatabaseOptions>() ?? new();
        ArgumentException.ThrowIfNullOrWhiteSpace(result.ConnectionStringName);
        var connection = configuration.GetConnectionString(result.ConnectionStringName);
        var explicitlyConfigured = configuration[$"{SectionName}:ConnectionStringName"] is not null || connection is not null;
        if (explicitlyConfigured && string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException($"ConnectionStrings:{result.ConnectionStringName} is required and cannot be empty.");
        result.Enabled = !string.IsNullOrWhiteSpace(connection);
        result.ConnectionString = connection ?? "";
        foreach (var name in new[] { result.MemoryConnectionStringName, result.GraphRagConnectionStringName, result.AclConnectionStringName, result.OperationsConnectionStringName })
            if (name is not null && (!result.Enabled || string.IsNullOrWhiteSpace(configuration.GetConnectionString(name))))
                throw new InvalidOperationException($"Database override '{name}' requires SQL mode and a nonempty connection string.");
        return result;
    }

    internal void ApplyOrleansDefaults(OrleansClusterOptions options, IConfiguration configuration, IFabrCoreOrleansProvider? provider)
    {
        if (!Enabled) return;
        if (configuration[$"{OrleansClusterOptions.SectionName}:ClusteringMode"] is null)
            options.ClusteringMode = provider?.Mode ?? ClusteringMode.SqlServer;
        if (options.ClusteringMode == ClusteringMode.SqlServer)
        {
            options.ConnectionString ??= ConnectionString;
            if (!AutoInitialize) options.AutoInitDatabase = false;
        }
    }
}
