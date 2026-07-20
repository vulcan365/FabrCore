using FabrCore.Host;
using FabrCore.Host.AzureStorage;
using FabrCore.Host.Configuration;
using FabrCore.Host.SqlServer;
using System.Reflection;

namespace FabrCore.Host.Tests;

[TestClass]
public class OrleansProviderTests
{
    [TestMethod]
    public void SqlServerProvider_HandlesSqlServerMode()
    {
        Assert.AreEqual(ClusteringMode.SqlServer, new SqlServerOrleansProvider().Mode);
    }

    [TestMethod]
    public void AzureStorageProvider_HandlesAzureStorageMode()
    {
        Assert.AreEqual(ClusteringMode.AzureStorage, new AzureStorageOrleansProvider().Mode);
    }

    [TestMethod]
    public void LocalhostProvider_HandlesLocalhostMode()
    {
        Assert.AreEqual(ClusteringMode.Localhost, new LocalhostOrleansProvider().Mode);
    }

    [TestMethod]
    [DataRow(ClusteringMode.SqlServer)]
    [DataRow(ClusteringMode.AzureStorage)]
    public void ConventionDiscovery_FindsProviderInModeNamedAssembly(ClusteringMode mode)
    {
        // AddFabrCoreServer discovers providers by loading "FabrCore.Host.<Mode>" and
        // instantiating its public parameterless IFabrCoreOrleansProvider implementation.
        var assembly = Assembly.Load($"FabrCore.Host.{mode}");

        var providerType = assembly.GetExportedTypes().FirstOrDefault(t =>
            !t.IsAbstract &&
            typeof(IFabrCoreOrleansProvider).IsAssignableFrom(t) &&
            t.GetConstructor(Type.EmptyTypes) is not null);

        Assert.IsNotNull(providerType, $"No IFabrCoreOrleansProvider found in FabrCore.Host.{mode}");

        var provider = (IFabrCoreOrleansProvider)Activator.CreateInstance(providerType)!;
        Assert.AreEqual(mode, provider.Mode);
    }

    [TestMethod]
    public void UseSqlServer_RegistersProvider()
    {
        var options = new FabrCoreServerOptions().UseSqlServer();
        Assert.IsInstanceOfType<SqlServerOrleansProvider>(options.OrleansProvider);
    }

    [TestMethod]
    public void UseAzureStorage_RegistersProvider()
    {
        var options = new FabrCoreServerOptions().UseAzureStorage();
        Assert.IsInstanceOfType<AzureStorageOrleansProvider>(options.OrleansProvider);
    }

    [TestMethod]
    public void SqlServerPackage_EmbedsAllOrleansScripts()
    {
        var assembly = typeof(SqlServerOrleansProvider).Assembly;
        var resources = assembly.GetManifestResourceNames();

        string[] expected =
        [
            "FabrCore.Host.SqlServer.SqlScripts.SQLServer-Main.sql",
            "FabrCore.Host.SqlServer.SqlScripts.SQLServer-Clustering.sql",
            "FabrCore.Host.SqlServer.SqlScripts.SQLServer-Persistence.sql",
            "FabrCore.Host.SqlServer.SqlScripts.SQLServer-Reminders.sql",
            "FabrCore.Host.SqlServer.SqlScripts.SQLServer-Streaming.sql"
        ];

        foreach (var resource in expected)
        {
            Assert.IsTrue(resources.Contains(resource),
                $"Missing embedded resource '{resource}'. Found: {string.Join(", ", resources)}");
        }
    }

    [TestMethod]
    public void StreamQueueNames_AreDeterministicAndValid()
    {
        var names = AzureStorageOrleansProvider.GetStreamQueueNames("fabrcore-service", 3);

        CollectionAssert.AreEqual(new[]
        {
            "fabrcore-service-streams-0",
            "fabrcore-service-streams-1",
            "fabrcore-service-streams-2"
        }, names);
    }

    [TestMethod]
    public void StreamQueueNames_SanitizesInvalidServiceIds()
    {
        // Azure queue names: 3-63 chars, lowercase alphanumeric, single dashes,
        // must start/end with a letter or number.
        var names = AzureStorageOrleansProvider.GetStreamQueueNames("My__Weird..Service!!", 1);

        foreach (var name in names)
        {
            StringAssert.Matches(name, new System.Text.RegularExpressions.Regex("^[a-z0-9]([a-z0-9]|-(?=[a-z0-9]))*$"),
                $"Queue name '{name}' is not a valid Azure queue name");
            Assert.IsTrue(name.Length is >= 3 and <= 63, $"Queue name '{name}' length out of range");
        }
    }
}
