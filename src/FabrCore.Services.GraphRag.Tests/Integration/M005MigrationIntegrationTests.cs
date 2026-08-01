using FabrCore.Services.GraphRag.Migrations;
using FabrCore.Services.GraphRag.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.GraphRag.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class M005MigrationIntegrationTests
{
    [TestMethod]
    public async Task Upgrade_AddsAndBackfillsScopedColumns_AndCanRerun()
    {
        var baseConnectionString = TestEnvironment.RequireDatabaseConnectionString();
        var databaseName = $"GraphRagM005_{Guid.NewGuid():N}";
        var masterBuilder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = "master"
        };
        var testBuilder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = databaseName
        };

        try
        {
            await using var master = new SqlConnection(masterBuilder.ConnectionString);
            await master.OpenAsync();
            await ExecuteNonQueryAsync(master, $"CREATE DATABASE [{databaseName}];");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Test database could not be created. {ex.Message}");
            return;
        }

        try
        {
            await using var connection = new SqlConnection(testBuilder.ConnectionString);
            await connection.OpenAsync();

            await ExecuteNonQueryAsync(connection, "CREATE SCHEMA grag;");
            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE grag.KnowledgeEntity (
                    EntityId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                    Name NVARCHAR(500) NOT NULL,
                    EntityType NVARCHAR(100) NOT NULL,
                    ScopeKey NVARCHAR(200) NOT NULL,
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                ) AS NODE;

                CREATE TABLE grag.KnowledgeRelationship (
                    RelationshipType NVARCHAR(200) NOT NULL,
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                ) AS EDGE;

                CREATE TABLE grag.KnowledgeCategory (
                    CategoryId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                    Name NVARCHAR(500) NOT NULL
                ) AS NODE;

                CREATE TABLE grag.BelongsTo (
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                ) AS EDGE;

                CREATE TABLE grag.CommunitySummary (
                    SummaryId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                    CategoryId UNIQUEIDENTIFIER NOT NULL,
                    Summary NVARCHAR(MAX) NOT NULL,
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                );

                CREATE TABLE grag.KnowledgeChunk (
                    ChunkId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                    ScopeKey NVARCHAR(200) NOT NULL
                );

                CREATE TABLE grag.SourceDocument (
                    DocumentId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
                    ScopeKey NVARCHAR(200) NOT NULL
                );

                CREATE TABLE grag.KnowledgeScope (
                    ScopeKey NVARCHAR(200) NOT NULL PRIMARY KEY,
                    Description NVARCHAR(MAX) NULL
                );

                INSERT INTO grag.KnowledgeEntity (Name, EntityType, ScopeKey)
                VALUES (N'Legacy entity', N'Test', N'legacy-scope');

                INSERT INTO grag.KnowledgeCategory (Name)
                VALUES (N'Legacy category');

                INSERT INTO grag.KnowledgeRelationship ($from_id, $to_id, RelationshipType)
                SELECT $node_id, $node_id, N'RelatedTo'
                FROM grag.KnowledgeEntity
                WHERE Name = N'Legacy entity';

                INSERT INTO grag.BelongsTo ($from_id, $to_id)
                SELECT entity.$node_id, category.$node_id
                FROM grag.KnowledgeEntity entity
                CROSS JOIN grag.KnowledgeCategory category
                WHERE entity.Name = N'Legacy entity';

                INSERT INTO grag.CommunitySummary (CategoryId, Summary)
                SELECT CategoryId, N'Legacy summary'
                FROM grag.KnowledgeCategory;
                """);

            await ApplyMigrationAsync(connection);
            await ApplyMigrationAsync(connection);

            await using var command = new SqlCommand("""
                SELECT
                    (SELECT COUNT(*) FROM sys.columns
                     WHERE object_id = OBJECT_ID(N'grag.KnowledgeEntity') AND name = N'CanonicalEntityId'),
                    (SELECT COUNT(*) FROM sys.columns
                     WHERE object_id = OBJECT_ID(N'grag.KnowledgeRelationship') AND name = N'ScopeKey'),
                    (SELECT COUNT(*) FROM sys.columns
                     WHERE object_id = OBJECT_ID(N'grag.BelongsTo') AND name = N'ScopeKey'),
                    (SELECT COUNT(*) FROM sys.columns
                     WHERE object_id = OBJECT_ID(N'grag.CommunitySummary') AND name = N'ScopeKey'),
                    (SELECT COUNT(*) FROM grag.KnowledgeEntity WHERE CanonicalEntityId IS NULL),
                    (SELECT COUNT(*) FROM grag.KnowledgeRelationship
                     WHERE ScopeKey IS NULL OR ScopeKey <> N'legacy-scope'),
                    (SELECT COUNT(*) FROM grag.BelongsTo
                     WHERE ScopeKey IS NULL OR ScopeKey <> N'legacy-scope'),
                    (SELECT COUNT(*) FROM grag.KnowledgeScope WHERE ScopeKey = N'legacy-scope');
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();

            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0), "CanonicalEntityId should be added.");
            Assert.AreEqual(1, reader.GetInt32(1), "Relationship ScopeKey should be added.");
            Assert.AreEqual(1, reader.GetInt32(2), "BelongsTo ScopeKey should be added.");
            Assert.AreEqual(1, reader.GetInt32(3), "CommunitySummary ScopeKey should be added.");
            Assert.AreEqual(0, reader.GetInt32(4), "Canonical identities should be backfilled.");
            Assert.AreEqual(0, reader.GetInt32(5), "Relationship scope should be backfilled.");
            Assert.AreEqual(0, reader.GetInt32(6), "Entity taxonomy assignment scope should be backfilled.");
            Assert.AreEqual(1, reader.GetInt32(7), "Discovered scope should be registered once.");
        }
        finally
        {
            await using var master = new SqlConnection(masterBuilder.ConnectionString);
            await master.OpenAsync();
            await ExecuteNonQueryAsync(master, $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END
                """);
        }
    }

    private static async Task ApplyMigrationAsync(SqlConnection connection)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await new M005_ScopedCanonicalKnowledge().ApplyAsync(
            connection, transaction, NullLogger.Instance);
        await transaction.CommitAsync();
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
