using FabrCore.Services.GraphRag.Migrations;
using FabrCore.Services.GraphRag.Tests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace FabrCore.Services.GraphRag.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class SchemaIntegrationTests
{
    [TestMethod]
    public async Task EnsureSchema_IsIdempotentAndAtLatestVersion()
    {
        var connectionString = TestEnvironment.RequireDatabaseConnectionString();

        await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString);
        await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
                (SELECT MAX(Version) FROM grag.SchemaVersion),
                (SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('grag')),
                (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('grag.KnowledgeEntity') AND name = 'Embedding');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(FabrCore.Services.GraphRag.Migrations.Migrations.Registered.Max(m => m.Version), reader.GetInt64(0));
        Assert.IsGreaterThanOrEqualTo(13, reader.GetInt32(1));
        Assert.AreEqual(1, reader.GetInt32(2));
    }

    [TestMethod]
    public async Task EnsureSchema_ConcurrentStartup_IsSerialized()
    {
        var connectionString = TestEnvironment.RequireDatabaseConnectionString();

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => GraphRagMigrationRunner.RunMigrationsAsync(connectionString)));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM grag.SchemaVersion", connection);
        Assert.AreEqual(FabrCore.Services.GraphRag.Migrations.Migrations.Registered.Count(), Convert.ToInt32(await command.ExecuteScalarAsync()));
    }
}
