using FabrCore.Host.Configuration;
using FabrCore.Host.Configuration.Cloud;
using FabrCore.Host.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Host.Tests;

/// <summary>
/// The database a host is given where it runs comes first: one Default for every feature, an override for one feature, and only then
/// what a cloud server sends. A publish meant for somewhere else can't move a host's data.
/// </summary>
[TestClass]
public sealed class HostDatabaseTests
{
    private const string Main = "Server=tcp:sql-main.database.windows.net;Database=fabrcore-host;Authentication=Active Directory Managed Identity";
    private const string Elsewhere = "Server=tcp:somewhere-else.database.windows.net;Database=other";

    private static IConfiguration Local(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static (string, string) Host(string key, string value) => ($"{FabrCoreHostDatabase.SectionName}:{key}", value);

    [TestMethod]
    public void Default_alone_gives_every_feature_the_same_database()
    {
        var settled = FabrCoreHostDatabase.Settle(Local(Host(FabrCoreHostDatabase.Default, Main)))!;

        Assert.AreEqual(Main, settled["ConnectionStrings:FabrCore"]);
        Assert.AreEqual("FabrCore", settled["FabrCore:Database:ConnectionStringName"]);
        Assert.AreEqual("SqlServer", settled["FabrCore:Orleans:ClusteringMode"]);
        Assert.AreEqual(Main, settled["FabrCore:Orleans:ConnectionString"]);
        Assert.AreEqual(Main, settled["FabrCore:Orleans:StorageConnectionString"]);
        foreach (var feature in new[] { "Acl", "Memory", "GraphRag", "Operations" })
            Assert.IsNull(settled[$"FabrCore:Database:{feature}ConnectionStringName"], feature);
    }

    [TestMethod]
    public void An_override_moves_only_its_own_feature()
    {
        const string acl = "Server=tcp:acl;Database=acl";
        const string state = "Server=tcp:state;Database=state";
        var settled = FabrCoreHostDatabase.Settle(Local(
            Host(FabrCoreHostDatabase.Default, Main), Host(FabrCoreHostDatabase.Acl, acl), Host(FabrCoreHostDatabase.State, state)))!;

        Assert.AreEqual("FabrCoreHostAcl", settled["FabrCore:Database:AclConnectionStringName"]);
        Assert.AreEqual(acl, settled["ConnectionStrings:FabrCoreHostAcl"]);
        Assert.IsNull(settled["FabrCore:Database:MemoryConnectionStringName"]);
        Assert.AreEqual(Main, settled["FabrCore:Orleans:ConnectionString"]);
        Assert.AreEqual(state, settled["FabrCore:Orleans:StorageConnectionString"]);
    }

    [TestMethod]
    public void Membership_alone_keeps_state_with_it_and_leaves_the_feature_database_alone()
    {
        var settled = FabrCoreHostDatabase.Settle(Local(Host(FabrCoreHostDatabase.Membership, Main)))!;

        Assert.AreEqual(Main, settled["FabrCore:Orleans:ConnectionString"]);
        Assert.AreEqual(Main, settled["FabrCore:Orleans:StorageConnectionString"]);
        Assert.IsFalse(settled.ContainsKey("ConnectionStrings:FabrCore"));
        Assert.IsFalse(settled.ContainsKey("FabrCore:Database:AclConnectionStringName"));
    }

    [TestMethod]
    public void Without_a_host_database_nothing_is_added()
    {
        Assert.IsNull(FabrCoreHostDatabase.Settle(Local()));

        var builder = new ConfigurationManager();
        FabrCoreHostDatabase.Apply(builder, builder);
        Assert.IsFalse(builder.Sources.OfType<HostDatabaseConfigurationSource>().Any());
    }

    [TestMethod]
    public void A_misnamed_or_empty_setting_stops_the_host()
    {
        Assert.Throws<InvalidOperationException>(() => FabrCoreHostDatabase.Settle(Local(Host("Acls", Main))));
        Assert.Throws<InvalidOperationException>(() => FabrCoreHostDatabase.Settle(Local(Host(FabrCoreHostDatabase.Default, " "))));
    }

    [TestMethod]
    [DataRow("FabrCore:HostDatabase")]
    [DataRow("FabrCore:HostDatabase:Default")]
    [DataRow("fabrcore:hostdatabase:acl")]
    public void A_cloud_server_can_never_set_it(string key) => Assert.IsTrue(CloudSettingsPolicy.IsBlocked(key));

    /// <summary>
    /// The real layering: a host that adds its own in-memory layer last (as Forge's does) before FabrCore inserts the cloud's. The
    /// backwards scan used to stop at that layer and put the cloud's above it.
    /// </summary>
    [TestMethod]
    public void Settings_from_a_cloud_server_cant_move_the_database()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [$"{FabrCoreHostDatabase.SectionName}:Default"] = Main });
        FabrCoreHostDatabase.Apply(builder.Configuration, builder.Configuration);
        var cloud = new CloudSettingsConfigurationProvider();
        cloud.Apply(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FabrCore"] = Elsewhere,
            ["ConnectionStrings:Other"] = Elsewhere,
            ["FabrCore:Database:AclConnectionStringName"] = "Other",
            ["FabrCore:Orleans:ClusteringMode"] = "AzureStorage",
            ["FabrCore:Orleans:ConnectionString"] = Elsewhere,
            ["FabrCore:Orleans:StorageConnectionString"] = Elsewhere,
            ["FabrCore:Orleans:ClusterId"] = "from-cloud"
        });
        CloudSettingsBootstrapper.Insert(builder.Configuration, new CloudSettingsConfigurationSource(cloud), NullLogger.Instance);
        IConfiguration configuration = builder.Configuration;

        Assert.AreEqual(Main, configuration["ConnectionStrings:FabrCore"]);
        Assert.AreEqual(Main, configuration["FabrCore:Orleans:ConnectionString"]);
        Assert.AreEqual(Main, configuration["FabrCore:Orleans:StorageConnectionString"]);
        Assert.AreEqual("SqlServer", configuration["FabrCore:Orleans:ClusteringMode"]);
        Assert.AreEqual("from-cloud", configuration["FabrCore:Orleans:ClusterId"], "Everything else still comes from the cloud server.");

        var database = FabrCoreDatabaseOptions.Resolve(configuration);
        Assert.IsTrue(database.Enabled);
        Assert.IsNull(database.AclConnectionStringName);
        var orleans = configuration.GetSection(OrleansClusterOptions.SectionName).Get<OrleansClusterOptions>()!;
        database.ApplyOrleansDefaults(orleans, configuration, provider: null);
        Assert.AreEqual(ClusteringMode.SqlServer, orleans.ClusteringMode);
        Assert.AreEqual(Main, orleans.EffectiveStorageConnectionString);
    }

    [TestMethod]
    public void Without_a_host_database_the_cloud_server_still_sets_it()
    {
        var builder = WebApplication.CreateBuilder();
        FabrCoreHostDatabase.Apply(builder.Configuration, builder.Configuration);
        var cloud = new CloudSettingsConfigurationProvider();
        cloud.Apply(new Dictionary<string, string?> { ["FabrCore:Orleans:ConnectionString"] = Elsewhere });
        CloudSettingsBootstrapper.Insert(builder.Configuration, new CloudSettingsConfigurationSource(cloud), NullLogger.Instance);

        Assert.AreEqual(Elsewhere, builder.Configuration["FabrCore:Orleans:ConnectionString"]);
    }

    [TestMethod]
    public void The_runtime_report_says_the_host_set_it()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [$"{FabrCoreHostDatabase.SectionName}:Default"] = Main });
        FabrCoreHostDatabase.Apply(configuration, configuration);
        var provider = new CloudSettingsConfigurationProvider();
        var sent = new Dictionary<string, string?> { ["FabrCore:Orleans:ClusteringMode"] = "AzureStorage" };
        provider.Apply(sent);
        CloudSettingsBootstrapper.Insert(configuration, new CloudSettingsConfigurationSource(provider), NullLogger.Instance);
        var cloud = new CloudSettingsState(provider, new() { ConfigurationVersion = "v1", Settings = sent });
        var runtime = new RuntimeConfigurationState(configuration, "host-database-test", [], cloud);
        using var services = new ServiceCollection().AddSingleton(new FabrCoreSettingsCatalog()).BuildServiceProvider();

        var row = runtime.Report(services).Settings.Single(s => s.Key == "FabrCore:Orleans:ClusteringMode");

        Assert.AreEqual("host", row.Source);
        Assert.AreEqual(FabrCoreHostDatabase.SectionName, row.SourceId);
        Assert.AreEqual("SqlServer", row.ResolvedValue);
        Assert.IsTrue(row.Overridden);
    }
}
