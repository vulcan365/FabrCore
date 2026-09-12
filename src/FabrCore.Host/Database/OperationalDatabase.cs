using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace FabrCore.Host.Database;

/// <summary>SQL storage owned by Host features, independent of Orleans persistence.</summary>
public sealed class OperationalDatabase(FabrCoreDatabaseOptions options, IConfiguration configuration)
{
    public Task<SqlConnection> OpenAsync(CancellationToken ct = default) => OpenAsync(true, ct);

    // A waiting signature-chain lock must not occupy a pooled connection that its owner
    // needs for the actual write. Otherwise enough concurrent callers can starve the pool.
    internal Task<SqlConnection> OpenCoordinationAsync(CancellationToken ct) => OpenAsync(false, ct);

    private async Task<SqlConnection> OpenAsync(bool pooled, CancellationToken ct)
    {
        var name = options.OperationsConnectionStringName ?? options.ConnectionStringName;
        var settings = new SqlConnectionStringBuilder(configuration.GetConnectionString(name) ?? throw new InvalidOperationException("Host operational features require the FabrCore database."));
        if (!pooled) settings.Pooling = false;
        var connection = new SqlConnection(settings.ConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static string Key(string value, string name, int length = 128)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > length) throw new ArgumentException($"{name} must contain 1–{length} characters.", name);
        return value;
    }

    internal static SqlCommand Command(SqlConnection connection, string sql, SqlTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal static async Task LockAsync(SqlConnection connection, SqlTransaction? transaction, string key, CancellationToken ct)
    {
        var resource = "FabrCore.Operations:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await using var command = Command(connection, """
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner=@owner, @LockTimeout=25000;
            IF @result < 0 THROW 51000, 'Operational storage lock could not be acquired.', 1;
            """, transaction, ("@resource", resource), ("@owner", transaction is null ? "Session" : "Transaction"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task InitializeAsync(bool initialize, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        if (initialize)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await LockAsync(connection, transaction, "schema-v1", ct);
            await using var command = Command(connection, Schema, transaction);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        await using var validate = Command(connection, """
            IF OBJECT_ID('fabrOps.SchemaVersion') IS NULL THROW 51000, 'Operational schema is missing.', 1;
            IF NOT EXISTS(SELECT 1 FROM fabrOps.SchemaVersion WHERE Version=1) THROW 51000, 'Operational migrations are pending.', 1;
            SELECT TOP(0) Id, Payload FROM fabrOps.Audit;
            SELECT TOP(0) TraceId, SegmentId, Sequence, RecordJson, SignatureJson, CertificateJson FROM fabrOps.Evidence;
            SELECT TOP(0) TraceId, SegmentId, NextSequence FROM fabrOps.EvidenceSequence;
            SELECT TOP(0) TraceId, Id, Payload FROM fabrOps.Attestation;
            SELECT TOP(0) Id, OwnerFingerprint, InstanceId, LeaseUntil, Terminal, CancelRequested, Payload FROM fabrOps.A2ATask;
            """);
        await validate.ExecuteNonQueryAsync(ct);
    }

    private const string Schema = """
        IF SCHEMA_ID('fabrOps') IS NULL EXEC('CREATE SCHEMA fabrOps');
        IF OBJECT_ID('fabrOps.SchemaVersion') IS NULL CREATE TABLE fabrOps.SchemaVersion(Version int NOT NULL PRIMARY KEY);
        IF NOT EXISTS(SELECT 1 FROM fabrOps.SchemaVersion WHERE Version=1)
        BEGIN
            CREATE TABLE fabrOps.Audit(
                Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Timestamp datetimeoffset NOT NULL, Category int NOT NULL, Outcome int NOT NULL,
                SubjectPrincipal nvarchar(200) COLLATE Latin1_General_100_CI_AS NULL,
                ResourcePrincipal nvarchar(200) COLLATE Latin1_General_100_CI_AS NULL,
                TraceId nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
                Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1));
            CREATE INDEX IX_Audit_Time ON fabrOps.Audit(Timestamp DESC, Id DESC);
            CREATE INDEX IX_Audit_Principal ON fabrOps.Audit(SubjectPrincipal, Timestamp DESC);
            CREATE INDEX IX_Audit_Trace ON fabrOps.Audit(TraceId, Timestamp DESC);
            CREATE TABLE fabrOps.EvidenceSequence(
                TraceId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SegmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                NextSequence bigint NOT NULL, PRIMARY KEY(TraceId,SegmentId));
            CREATE TABLE fabrOps.Evidence(
                TraceId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                SegmentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sequence bigint NOT NULL CHECK(Sequence>0),
                RecordId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Timestamp datetimeoffset NOT NULL,
                RecordJson nvarchar(max) NOT NULL CHECK(ISJSON(RecordJson)=1),
                SignatureJson nvarchar(max) NULL, CertificateJson nvarchar(max) NULL,
                PRIMARY KEY(TraceId,SegmentId,Sequence), UNIQUE(TraceId,RecordId));
            CREATE TABLE fabrOps.Attestation(
                TraceId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1), PRIMARY KEY(TraceId,Id));
            CREATE TABLE fabrOps.A2ATask(
                Id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                OwnerFingerprint nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                InstanceId uniqueidentifier NOT NULL, LeaseUntil datetime2 NULL,
                Terminal bit NOT NULL, CancelRequested bit NOT NULL DEFAULT(0),
                SavedAt datetime2 NOT NULL, Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1));
            CREATE INDEX IX_A2ATask_Lease ON fabrOps.A2ATask(Terminal,LeaseUntil);
            CREATE INDEX IX_A2ATask_Retention ON fabrOps.A2ATask(Terminal,SavedAt);
            INSERT fabrOps.SchemaVersion VALUES(1);
        END
        """;
}
