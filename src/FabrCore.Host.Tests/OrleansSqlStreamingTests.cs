using FabrCore.Host.Configuration;
using FabrCore.Host.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using System.Text.RegularExpressions;

namespace FabrCore.Host.Tests;

[TestClass]
public class OrleansSqlStreamingTests
{
    [TestMethod]
    public void AdoNetMode_UsesClusteringDatabaseForQueues()
    {
        const string connection = "Server=localhost;Database=cluster;Integrated Security=true;TrustServerCertificate=true";
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseOrleans(silo =>
            new SqlServerOrleansProvider().ConfigureSilo(silo, new OrleansClusterOptions
            {
                ConnectionString = connection,
                StorageConnectionString = "Server=localhost;Database=state;Integrated Security=true",
                SqlServerStreams = SqlServerStreamMode.AdoNet
            }, new ConfigurationBuilder().Build(), NullLogger.Instance)).Build();
        var options = host.Services.GetRequiredService<IOptionsMonitor<AdoNetStreamOptions>>()
            .Get(FabrCoreOrleansConstants.StreamProviderName);
        Assert.AreEqual(connection, options.ConnectionString);
        Assert.AreEqual("Microsoft.Data.SqlClient", options.Invariant);
        Assert.AreEqual(SqlServerStreamMode.Memory, new OrleansClusterOptions().SqlServerStreams);
    }

    [TestMethod]
    [TestCategory("SqlIntegration")]
    public async Task StreamingMigration_IsRepeatableAndPreservesQueuedMessages()
    {
        var connectionString = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set FABRCORE_SQL_TEST_CONNECTION_STRING to run SQL integration tests.");
        }

        // Only this randomly named test database is created/removed; the configured database is never modified.
        var database = "FabrCoreStreamTest_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var admin = new SqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await Execute(admin, $"CREATE DATABASE [{database}]");
        try
        {
            builder.InitialCatalog = database;
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await ApplyScript(connection, "SQLServer-Main.sql");
            await ApplyScript(connection, "SQLServer-Streaming.sql");
            await Execute(connection, "EXEC orlns.QueueStreamMessage 'test', 'fabrcoreStreams', 'queue-1', 0x1234, 600");
            var firstId = await Scalar(connection, "SELECT MIN(MessageId) FROM orlns.OrleansStreamMessage");

            // Simulate an old stored procedure/query definition, then apply the package migration again.
            await Execute(connection, "CREATE OR ALTER PROCEDURE orlns.EvictStreamDeadLetters AS SELECT 1");
            await Execute(connection, "UPDATE orlns.OrleansQuery SET QueryText = 'legacy' WHERE QueryKey = 'EvictStreamDeadLettersKey'");
            await ApplyScript(connection, "SQLServer-Streaming.sql");
            Assert.AreEqual(1L, await Scalar(connection, "SELECT COUNT_BIG(*) FROM orlns.OrleansStreamMessage WHERE Payload = 0x1234"));
            Assert.AreEqual(6L, await Scalar(connection, "SELECT COUNT_BIG(*) FROM orlns.OrleansQuery WHERE QueryText LIKE 'EXECUTE orlns.%'"));

            await Execute(connection, "EXEC orlns.QueueStreamMessage 'test', 'fabrcoreStreams', 'queue-1', 0x5678, 600");
            Assert.IsTrue(await Scalar(connection, "SELECT MAX(MessageId) FROM orlns.OrleansStreamMessage") > firstId);
            await Execute(connection, "EXEC orlns.GetStreamMessages 'test', 'fabrcoreStreams', 'queue-1', 10, 5, 60, 604800, 10, 1000");
            Assert.AreEqual(2L, await Scalar(connection, "SELECT COUNT_BIG(*) FROM orlns.OrleansStreamMessage WHERE Dequeued = 1"));

            // A rollback must not leave a queued message behind.
            await Execute(connection, "BEGIN TRANSACTION; EXEC orlns.QueueStreamMessage 'test', 'fabrcoreStreams', 'queue-1', 0xFFFF, 600; ROLLBACK");
            Assert.AreEqual(0L, await Scalar(connection, "SELECT COUNT_BIG(*) FROM orlns.OrleansStreamMessage WHERE Payload = 0xFFFF"));
        }
        finally
        {
            await Execute(admin, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    private static async Task ApplyScript(SqlConnection connection, string name)
    {
        using var stream = typeof(SqlServerOrleansProvider).Assembly.GetManifestResourceStream("FabrCore.Host.SqlServer.SqlScripts." + name)!;
        using var reader = new StreamReader(stream);
        foreach (var batch in Regex.Split(await reader.ReadToEndAsync(), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await Execute(connection, batch);
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
