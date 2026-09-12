using System.Text.Json;
using FabrCore.Host.A2A;
using FabrCore.Host.A2A.Protocol;
using FabrCore.Host.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Database;

/// <summary>Shared task snapshots with fenced ownership, leases, cancellation and terminal retention.</summary>
public sealed class SqlA2ATaskStore(OperationalDatabase database, IOptions<A2AOptions> options) : IA2ATaskStore, IDurableA2ATaskStore
{
    private readonly Guid instance = Guid.NewGuid();
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(10);
    private const int LeaseSeconds = 60;

    public async ValueTask SaveAsync(A2ATask task, CancellationToken cancellationToken = default)
    {
        OperationalDatabase.Key(task.Id, nameof(task.Id));
        if (task.Metadata?.TryGetValue(A2ATaskOwnership.Key, out var fingerprint) != true || fingerprint.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("A durable A2A task requires server-owned identity metadata.");
        var owner = OperationalDatabase.Key(fingerprint.GetString()!, "owner");
        var payload = JsonSerializer.Serialize(task);
        if (payload.Length > 4 * 1024 * 1024) throw new InvalidOperationException("A2A task snapshot exceeds 4 MiB.");
        var terminal = A2ATaskStates.IsTerminal(task.Status.State);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await OperationalDatabase.LockAsync(connection, transaction, "task:" + task.Id, cancellationToken);
        await using var command = OperationalDatabase.Command(connection, """
            DECLARE @now datetime2=SYSUTCDATETIME();
            IF EXISTS(SELECT 1 FROM fabrOps.A2ATask WHERE Id=@id)
            BEGIN
                IF EXISTS(SELECT 1 FROM fabrOps.A2ATask WHERE Id=@id AND OwnerFingerprint<>@owner) THROW 51000, 'Task ownership cannot change.', 1;
                IF EXISTS(SELECT 1 FROM fabrOps.A2ATask WHERE Id=@id AND Terminal=1)
                BEGIN
                    IF EXISTS(SELECT 1 FROM fabrOps.A2ATask WHERE Id=@id AND Payload COLLATE Latin1_General_100_BIN2<>@payload COLLATE Latin1_General_100_BIN2)
                        THROW 51000, 'Terminal task snapshots are immutable.', 1;
                    RETURN;
                END
                IF EXISTS(SELECT 1 FROM fabrOps.A2ATask WHERE Id=@id AND (InstanceId<>@instance OR LeaseUntil<=@now))
                    THROW 51000, 'Task execution lease was lost.', 1;
                UPDATE fabrOps.A2ATask SET Payload=@payload,Terminal=@terminal,SavedAt=@now,
                    LeaseUntil=CASE WHEN @terminal=1 THEN NULL ELSE DATEADD(second,@lease,@now) END WHERE Id=@id;
            END
            ELSE INSERT fabrOps.A2ATask(Id,OwnerFingerprint,InstanceId,LeaseUntil,Terminal,CancelRequested,SavedAt,Payload)
                VALUES(@id,@owner,@instance,CASE WHEN @terminal=1 THEN NULL ELSE DATEADD(second,@lease,@now) END,@terminal,0,@now,@payload);
            """, transaction, ("@id", task.Id), ("@owner", owner), ("@instance", instance), ("@terminal", terminal), ("@lease", LeaseSeconds), ("@payload", payload));
        await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    private async Task Maintain(SqlConnection connection, CancellationToken ct)
    {
        // Interrupted work is marked failed, never replayed automatically: its external effects may already have happened.
        await using var command = OperationalDatabase.Command(connection, """
            UPDATE fabrOps.A2ATask SET Terminal=1, LeaseUntil=NULL, SavedAt=SYSUTCDATETIME(),
                Payload=JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(Payload,'$.Status.State','failed'),
                    '$.Status.Timestamp',CONVERT(varchar(33),SYSUTCDATETIME(),126)+'Z'),
                    '$.Metadata."fabrcore.interruption"','The execution host stopped renewing its lease. Inspect external effects before retrying.')
            WHERE Terminal=0 AND LeaseUntil<SYSUTCDATETIME();
            DELETE TOP(1000) FROM fabrOps.A2ATask WHERE Terminal=1 AND SavedAt<DATEADD(second,-@retention,SYSUTCDATETIME());
            """, null, ("@retention", checked((int)options.Value.Tasks.Retention.TotalSeconds)));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<A2ATask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        OperationalDatabase.Key(taskId, nameof(taskId));
        await using var connection = await database.OpenAsync(cancellationToken);
        await Maintain(connection, cancellationToken);
        await using var command = OperationalDatabase.Command(connection, "SELECT Payload FROM fabrOps.A2ATask WHERE Id=@id AND (Terminal=0 OR SavedAt>=DATEADD(second,-@retention,SYSUTCDATETIME()));", null,
            ("@id", taskId), ("@retention", checked((int)options.Value.Tasks.Retention.TotalSeconds)));
        return await command.ExecuteScalarAsync(cancellationToken) is string json ? JsonSerializer.Deserialize<A2ATask>(json) : null;
    }

    public async ValueTask<IReadOnlyList<A2ATask>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken); await Maintain(connection, cancellationToken);
        await using var command = OperationalDatabase.Command(connection, "SELECT Payload FROM fabrOps.A2ATask WHERE Terminal=0 OR SavedAt>=DATEADD(second,-@retention,SYSUTCDATETIME()) ORDER BY SavedAt DESC,Id;", null,
            ("@retention", checked((int)options.Value.Tasks.Retention.TotalSeconds)));
        var result = new List<A2ATask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(JsonSerializer.Deserialize<A2ATask>(reader.GetString(0))!);
        return result;
    }

    public async ValueTask<bool> RequestCancellationAsync(string taskId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct); await Maintain(connection, ct);
        await using var command = OperationalDatabase.Command(connection, "UPDATE fabrOps.A2ATask SET CancelRequested=1 WHERE Id=@id AND Terminal=0 AND LeaseUntil>SYSUTCDATETIME();", null, ("@id", taskId));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
    public async ValueTask<bool> IsCancellationRequestedAsync(string taskId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = OperationalDatabase.Command(connection, "SELECT CancelRequested FROM fabrOps.A2ATask WHERE Id=@id AND InstanceId=@instance AND Terminal=0 AND LeaseUntil>SYSUTCDATETIME();", null, ("@id", taskId), ("@instance", instance));
        return await command.ExecuteScalarAsync(ct) is bool cancel ? cancel : throw new InvalidOperationException("Task lease is no longer owned by this executor.");
    }
}
