using FabrCore.Core.Auditing;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.A2A;
using FabrCore.Host.A2A.Protocol;
using FabrCore.Host.Configuration;
using FabrCore.Host.Database;
using FabrCore.Host.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace FabrCore.Host.Tests;

[TestClass, DoNotParallelize, TestCategory("SqlMode")]
public sealed class OperationalSqlTests
{
    private static string? connectionString;
    private static string? databaseName;
    private static string? masterString;
    private static OperationalDatabase Database()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:FabrCore"] = connectionString }).Build();
        return new(FabrCoreDatabaseOptions.Resolve(config), config);
    }
    [ClassInitialize]
    public static async Task Initialize(TestContext context)
    {
        var password = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_PASSWORD");
        if (string.IsNullOrEmpty(password)) Assert.Inconclusive("SQL integration tests require the isolated SQL test container.");
        var builder = new SqlConnectionStringBuilder { DataSource = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_SERVER") ?? "localhost", InitialCatalog = "master", UserID = "sa", Password = password, TrustServerCertificate = true };
        masterString = builder.ConnectionString;
        databaseName = "FabrCoreOpsTest_" + Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(masterString); await connection.OpenAsync();
        await using var create = new SqlCommand($"CREATE DATABASE [{databaseName}]", connection); await create.ExecuteNonQueryAsync();
        builder.InitialCatalog = databaseName; connectionString = builder.ConnectionString;
        await Task.WhenAll(Database().InitializeAsync(true), Database().InitializeAsync(true));
        await Database().InitializeAsync(false);
    }
    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (databaseName is null) return;
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(masterString); await connection.OpenAsync();
        await using var drop = new SqlCommand($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];", connection); await drop.ExecuteNonQueryAsync();
    }
    private static SqlAuditProvider Audit(AuditOptions? options = null) => new(Database(), Options.Create(options ?? new()), NullLogger<SqlAuditProvider>.Instance);
    private static SqlA2ATaskStore Tasks() => new(Database(), Options.Create(new A2AOptions()));
    private static A2ATask TaskSnapshot() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Status = new() { State = A2ATaskStates.Working },
        Metadata = new() { [A2ATaskOwnership.Key] = JsonSerializer.SerializeToElement("owner-one") }
    };

    [TestMethod]
    public async Task AuditPersistsFiltersAndPagesWithoutDuplicateEvents()
    {
        var provider = Audit(); var trace = Guid.NewGuid().ToString("N"); var timestamp = DateTimeOffset.UtcNow;
        var calls = 0; provider.OnAuditEventRecorded += _ => throw new Exception("subscriber"); provider.OnAuditEventRecorded += _ => calls++;
        for (var i = 0; i < 4; i++)
        {
            var item = new AuditEvent { Id = trace + i, Timestamp = timestamp, TraceId = trace, SubjectPrincipal = "Alice", Category = AuditCategory.AclManagement, Outcome = AuditOutcome.Success };
            await provider.RecordAsync(item); await provider.RecordAsync(item);
        }
        await provider.RecordAsync(new() { TraceId = trace, Category = AuditCategory.AclDecision, Outcome = AuditOutcome.Success });
        Assert.AreEqual(4, calls); Assert.AreEqual(0L, provider.FailedWrites);
        var restarted = Audit(); var first = await restarted.GetEventsAsync(new() { TraceId = trace, SubjectPrincipal = "alice", Limit = 2 });
        var second = await restarted.GetEventsAsync(new() { TraceId = trace, Before = first[^1].Timestamp, BeforeId = first[^1].Id, Limit = 2 });
        Assert.AreEqual(4, first.Concat(second).Select(e => e.Id).Distinct().Count());
        Assert.AreEqual(trace + "3", first[0].Id);
    }

    [TestMethod]
    public async Task AuditConflictingRetryIsObservableWithoutChangingStoredRecord()
    {
        var provider = Audit();
        var item = new AuditEvent { Category = AuditCategory.AclManagement, Outcome = AuditOutcome.Success, Reason = "original" };
        await provider.RecordAsync(item);
        item.Reason = "replacement";
        await provider.RecordAsync(item);
        Assert.AreEqual(1L, provider.FailedWrites);
        Assert.IsNotNull(provider.LastFailureUtc);
        Assert.AreEqual("original", (await provider.GetEventsAsync(new() { Since = item.Timestamp, Limit = 1000 })).Single(e => e.Id == item.Id).Reason);
    }

    [TestMethod]
    public async Task ConcurrentSignedEvidenceSurvivesRestartAndRejectsTampering()
    {
        using var signer = new LocalCertificateVerifiableExecutionSigner(); var trace = Guid.NewGuid().ToString("N");
        var stores = new[] { new SqlVerifiableExecutionStore(Database()), new SqlVerifiableExecutionStore(Database()) };
        var recorders = stores.Select(s => new VerifiableExecutionRecorder(s, signer, new VerifiableExecutionVerifier(), Options.Create(new VerifiableExecutionOptions { Enabled = true }), NullLogger<VerifiableExecutionRecorder>.Instance)).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => recorders[i%2].RecordAsync(new() { TraceId = trace, AgentHandle = "alice:worker", Subject = "operation-" + i })));
        var restarted = new SqlVerifiableExecutionStore(Database()); var bundle = await restarted.GetBundleAsync(trace);
        Assert.AreEqual(12, bundle.Records.Count); Assert.AreEqual(12, bundle.Signatures.Count); Assert.AreEqual(1, bundle.Certificates.Count);
        CollectionAssert.AreEqual(Enumerable.Range(1,12).Select(i => (long)i).ToArray(), bundle.Records.Select(r => r.Sequence).ToArray());
        var verified = await new VerifiableExecutionVerifier().VerifyAsync(bundle); Assert.IsTrue(verified.IsValid, string.Join(";", verified.Errors));
        var record = bundle.Records[0]; var signature = bundle.Signatures[0];
        await restarted.AppendRecordAsync(record, signature, bundle.Certificates[0]);
        record.Subject = "tampered";
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => restarted.AppendRecordAsync(record, signature, bundle.Certificates[0]));
        Assert.IsFalse((await new VerifiableExecutionVerifier().VerifyAsync(bundle)).IsValid);
        Assert.IsTrue((await new VerifiableExecutionVerifier().VerifyAsync(await restarted.GetBundleAsync(trace))).IsValid);
    }

    [TestMethod]
    public async Task UnsignedEvidenceAndAttestationsRemainDurableAndImmutable()
    {
        var store = new SqlVerifiableExecutionStore(Database()); var trace = Guid.NewGuid().ToString("N");
        var record = new VerifiableExecutionRecord { TraceId = trace, Sequence = await store.GetNextSequenceAsync(trace,"default") };
        await store.AppendRecordAsync(record, null, null);
        var attestation = new VerifiableExecutionAttestation { TraceId = trace, ParentRecordId = record.Id, TerminalStatus = "completed" };
        await store.AddAttestationAsync(attestation); await store.AddAttestationAsync(attestation);
        var bundle = await new SqlVerifiableExecutionStore(Database()).GetBundleAsync(trace);
        Assert.AreEqual(1,bundle.Attestations.Count); Assert.AreEqual(ExecutionTrustLevel.Unsigned,(await new VerifiableExecutionVerifier().VerifyAsync(bundle)).TrustLevel);
        attestation.TerminalStatus = "changed";
        await Assert.ThrowsExactlyAsync<SqlException>(() => store.AddAttestationAsync(attestation));
        Assert.AreEqual("completed",(await store.GetBundleAsync(trace)).Attestations[0].TerminalStatus);
    }

    [TestMethod]
    public async Task TaskSnapshotsAreSharedCancellableAndTerminalStatesCannotRegress()
    {
        var first = Tasks(); var second = Tasks(); var task = TaskSnapshot(); await first.SaveAsync(task);
        Assert.AreEqual(A2ATaskStates.Working,(await second.GetAsync(task.Id))!.Status.State);
        Assert.IsTrue(await second.RequestCancellationAsync(task.Id)); Assert.IsTrue(await first.IsCancellationRequestedAsync(task.Id));
        await Assert.ThrowsExactlyAsync<SqlException>(async () => await second.SaveAsync(task));
        task.Status.State = A2ATaskStates.Canceled; await first.SaveAsync(task); await second.SaveAsync(task);
        task.Status.State = A2ATaskStates.Working;
        await Assert.ThrowsExactlyAsync<SqlException>(async () => await first.SaveAsync(task));
        Assert.AreEqual(A2ATaskStates.Canceled,(await second.GetAsync(task.Id))!.Status.State);
        Assert.IsFalse(await second.RequestCancellationAsync(task.Id));
    }

    [TestMethod]
    public async Task LostLeasesBecomeFailedWithoutReplayingAndFenceOldWriters()
    {
        var first = Tasks(); var task = TaskSnapshot(); await first.SaveAsync(task);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(); await using var command = new SqlCommand("UPDATE fabrOps.A2ATask SET LeaseUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE Id=@id",connection);
            command.Parameters.AddWithValue("@id",task.Id); await command.ExecuteNonQueryAsync();
        }
        var recovered = (await Tasks().GetAsync(task.Id))!;
        Assert.AreEqual(A2ATaskStates.Failed,recovered.Status.State); Assert.IsTrue(recovered.Metadata!.ContainsKey("fabrcore.interruption"));
        task.Status.State = A2ATaskStates.Completed;
        await Assert.ThrowsExactlyAsync<SqlException>(async () => await first.SaveAsync(task));
    }

    [TestMethod]
    public async Task RetentionExpiresOnlyTerminalTasksAndOwnershipCannotBeChanged()
    {
        var store = Tasks(); var active = TaskSnapshot(); await store.SaveAsync(active);
        var completed = TaskSnapshot(); completed.Status.State = A2ATaskStates.Completed; await store.SaveAsync(completed);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(); await using var command = new SqlCommand("UPDATE fabrOps.A2ATask SET SavedAt=DATEADD(hour,-2,SYSUTCDATETIME()) WHERE Id IN(@active,@completed)",connection);
            command.Parameters.AddWithValue("@active",active.Id); command.Parameters.AddWithValue("@completed",completed.Id); await command.ExecuteNonQueryAsync();
        }
        Assert.IsNull(await Tasks().GetAsync(completed.Id)); Assert.IsNotNull(await Tasks().GetAsync(active.Id));
        active.Metadata![A2ATaskOwnership.Key] = JsonSerializer.SerializeToElement("different-owner");
        await Assert.ThrowsExactlyAsync<SqlException>(async () => await store.SaveAsync(active));
    }
}
