using FabrCore.Host.Database;
using FabrCore.Services.GraphRag;
using FabrCore.Services.GraphRag.Migrations;
using FabrCore.Services.Memory.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Tests;

[TestClass, DoNotParallelize, TestCategory("SqlMode")]
public sealed class DatabaseSchemaTests
{
    private static string connectionString = null!;
    private static string masterString = null!;
    private static string databaseName = null!;

    [ClassInitialize]
    public static async Task Initialize(TestContext context)
    {
        var password = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_PASSWORD");
        if (string.IsNullOrEmpty(password)) Assert.Inconclusive("Isolated SQL test credentials are required.");
        var settings = new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_SERVER") ?? "localhost",
            InitialCatalog = "master", UserID = "sa", Password = password, TrustServerCertificate = true
        };
        masterString = settings.ConnectionString;
        databaseName = "FabrCoreSchemaTest_" + Guid.NewGuid().ToString("N");
        await Execute(masterString, $"CREATE DATABASE [{databaseName}]");
        settings.InitialCatalog = databaseName;
        connectionString = settings.ConnectionString;
        await StartSchema(true);
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (databaseName is null) return;
        SqlConnection.ClearAllPools();
        await Execute(masterString, $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
    }

    private static async Task StartSchema(bool initialize)
    {
        await using var app = DatabaseModeTests.BuildHost("Development", connectionString, initialize);
        await app.Services.GetServices<IHostedService>().OfType<DatabaseSchemaHostedService>().Single().StartAsync(default);
    }

    [TestMethod]
    public async Task CompleteSchemaSupportsBothStartupModes()
    {
        await StartSchema(false);
        await StartSchema(true);
    }

    [TestMethod]
    [DataRow("grag", "KnowledgeChunk")]
    [DataRow("grag", "SourceDocument")]
    [DataRow("mem", "MemoryExtractionReceipt")]
    [DataRow("acl", "GroupMember")]
    [DataRow("fabrOps", "Audit")]
    public async Task MissingTableFailsBeforeServingRequests(string schema, string table)
    {
        await Execute(connectionString, $"EXEC sp_rename '{schema}.{table}', '{table}_held'");
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartSchema(false));
            StringAssert.Contains(error.Message, $"{schema}.{table}");
        }
        finally { await Execute(connectionString, $"EXEC sp_rename '{schema}.{table}_held', '{table}'"); }
    }

    [TestMethod]
    [DataRow("grag.SourceDocument", "SourceTitle")]
    [DataRow("acl.Principal", "Description")]
    [DataRow("fabrOps.A2ATask", "SavedAt")]
    public async Task MissingColumnIsIdentified(string table, string column)
    {
        await Execute(connectionString, $"EXEC sp_rename '{table}.{column}', '{column}_held', 'COLUMN'");
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartSchema(false));
            StringAssert.Contains(error.Message, $"{table}.{column}");
        }
        finally { await Execute(connectionString, $"EXEC sp_rename '{table}.{column}_held', '{column}', 'COLUMN'"); }
    }

    [TestMethod]
    public async Task MissingRequiredMemoryIndexIsIdentified()
    {
        await Execute(connectionString, "EXEC sp_rename 'mem.MemoryEntity.IX_MemoryEntity_Scope_Name_Type', 'IX_MemoryEntity_held', 'INDEX'");
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartSchema(false));
            StringAssert.Contains(error.Message, "IX_MemoryEntity_Scope_Name_Type");
        }
        finally { await Execute(connectionString, "EXEC sp_rename 'mem.MemoryEntity.IX_MemoryEntity_held', 'IX_MemoryEntity_Scope_Name_Type', 'INDEX'"); }
    }

    [TestMethod]
    public async Task ExistingWrongVectorDimensionsAreRejectedWithoutRebuilding()
    {
        await Execute(connectionString, "ALTER TABLE mem.MemoryChunk DROP COLUMN Embedding; ALTER TABLE mem.MemoryChunk ADD Embedding VECTOR(12)");
        try
        {
            foreach (var initialize in new[] { false, true })
            {
                var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartSchema(initialize));
                StringAssert.Contains(error.Message, "mem.MemoryChunk.Embedding");
                StringAssert.Contains(error.Message, "12");
            }
        }
        finally { await Execute(connectionString, "ALTER TABLE mem.MemoryChunk DROP COLUMN Embedding; ALTER TABLE mem.MemoryChunk ADD Embedding VECTOR(1536)"); }
    }

    [TestMethod]
    public async Task OrdinaryTableCannotSubstituteForGraphEdge()
    {
        await Execute(connectionString, "EXEC sp_rename 'grag.BelongsTo', 'BelongsTo_held'; CREATE TABLE grag.BelongsTo (ScopeKey nvarchar(200))");
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartSchema(false));
            StringAssert.Contains(error.Message, "expected edge");
        }
        finally { await Execute(connectionString, "DROP TABLE grag.BelongsTo; EXEC sp_rename 'grag.BelongsTo_held', 'BelongsTo'"); }
    }

    [TestMethod]
    public async Task MissingOperationalMigrationRecordRequiresProvisioning()
    {
        await Execute(connectionString, "DELETE fabrOps.SchemaVersion WHERE Version=1");
        try
        {
            var error = await Assert.ThrowsExactlyAsync<SqlException>(() => StartSchema(false));
            StringAssert.Contains(error.Message, "fabrOps.SchemaVersion");
            StringAssert.Contains(error.Message, "Provision the supported operational schema");
        }
        finally { await Execute(connectionString, "INSERT fabrOps.SchemaVersion(Version) VALUES(1)"); }
    }

    [TestMethod]
    public async Task PendingMigrationRequiresProvisioning()
    {
        await Execute(connectionString, "DELETE grag.SchemaVersion WHERE Version=8");
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => StartSchema(false));
            StringAssert.Contains(error.Message, "migrations are pending");
        }
        finally { await StartSchema(true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SchemaLockCanWaitBeyondDefaultCommandTimeout(bool graph)
    {
        await using var holder = new SqlConnection(connectionString);
        await holder.OpenAsync();
        var resource = graph ? "grag:schema-migration" : "FabrCore.Services.Memory.SchemaInitialization";
        await Lock(holder, resource, true);
        var pending = Ensure(graph, CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(33));
            Assert.IsFalse(pending.IsCompleted, "Initialization must still be waiting for its 60-second lock budget.");
        }
        finally { await Lock(holder, resource, false); }
        await pending.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CanceledLockWaitDoesNotPreventSubsequentStartup(bool graph)
    {
        await using var holder = new SqlConnection(connectionString);
        await holder.OpenAsync();
        var resource = graph ? "grag:schema-migration" : "FabrCore.Services.Memory.SchemaInitialization";
        await Lock(holder, resource, true);
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try { await Ensure(graph, ct.Token).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
        }
        finally { await Lock(holder, resource, false); }
        await Ensure(graph, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static Task Ensure(bool graph, CancellationToken ct) => graph
        ? GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, null, ct)
        : MemorySchemaInitializer.EnsureSchemaAsync(connectionString, 1536, null, ct);

    [TestMethod]
    public async Task BothSchemaLocksRespectTheirSixtySecondBudget()
    {
        await Task.WhenAll(Check(false), Check(true));
        static async Task Check(bool graph)
        {
            await using var holder = new SqlConnection(connectionString);
            await holder.OpenAsync();
            var resource = graph ? "grag:schema-migration" : "FabrCore.Services.Memory.SchemaInitialization";
            await Lock(holder, resource, true);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                try { await Ensure(graph, default).WaitAsync(TimeSpan.FromSeconds(80)); Assert.Fail("Expected lock timeout."); }
                catch (SqlException ex) when (!graph) { Assert.AreEqual(51000, ex.Number); }
                catch (InvalidOperationException ex) when (graph) { StringAssert.Contains(ex.Message, "could not acquire applock"); }
                Assert.IsTrue(timer.Elapsed >= TimeSpan.FromSeconds(55), "Command timeout must not cut the lock wait short.");
            }
            finally { await Lock(holder, resource, false); }
        }
    }

    [TestMethod]
    public async Task CancellationRollsBackMigrationDdlAndVersionRecord()
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var session = new SqlCommand("SELECT @@SPID", connection);
        var sessionId = Convert.ToInt32(await session.ExecuteScalarAsync());
        using var ct = new CancellationTokenSource();
        var migration = new CanceledMigration();
        var applying = GraphRagMigrationRunner.ApplyOneAsync(connection, migration, null, ct.Token);
        await migration.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Observe an in-flight command, rather than racing cancellation against the
        // driver's send/setup path immediately after the DDL continuation resumes.
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync();
        await using var request = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@id AND wait_type='WAITFOR'", observer);
        request.Parameters.AddWithValue("@id", sessionId);
        using var started = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Convert.ToInt32(await request.ExecuteScalarAsync(started.Token)) == 0)
            await Task.Delay(25, started.Token);
        await ct.CancelAsync();
        try { await applying.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("Expected cancellation."); }
        catch (OperationCanceledException) { }
        await using var check = new SqlCommand("SELECT CASE WHEN OBJECT_ID('grag.CancellationProbe') IS NULL AND NOT EXISTS(SELECT 1 FROM grag.SchemaVersion WHERE Version=999) THEN 1 ELSE 0 END", connection);
        Assert.AreEqual(1, await check.ExecuteScalarAsync());
        await StartSchema(false);
    }

    private sealed class CanceledMigration : IGraphRagMigration
    {
        public long Version => 999;
        public string Description => "Test transaction rollback";
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger) => ApplyAsync(connection, transaction, logger, default);
        public async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, ILogger logger, CancellationToken ct)
        {
            await using var create = new SqlCommand("CREATE TABLE grag.CancellationProbe(Id int)", connection, transaction);
            await create.ExecuteNonQueryAsync(ct);
            Created.SetResult();
            await using var wait = new SqlCommand("WAITFOR DELAY '00:01:00'", connection, transaction) { CommandTimeout = 90 };
            await wait.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task Lock(SqlConnection connection, string resource, bool acquire)
    {
        await using var cmd = new SqlCommand(acquire
            ? "EXEC sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session'"
            : "EXEC sp_releaseapplock @Resource=@resource,@LockOwner='Session'", connection);
        cmd.Parameters.AddWithValue("@resource", resource);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Execute(string connection, string sql)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await using var cmd = new SqlCommand(sql, db) { CommandTimeout = 90 };
        await cmd.ExecuteNonQueryAsync();
    }
}
