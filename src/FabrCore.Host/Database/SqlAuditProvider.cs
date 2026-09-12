using FabrCore.Core.Auditing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace FabrCore.Host.Database;

public sealed class SqlAuditProvider(OperationalDatabase database, IOptions<AuditOptions> options, ILogger<SqlAuditProvider> logger) : IAuditProvider
{
    public AuditOptions Options { get; } = options.Value;
    public event Action<AuditEvent>? OnAuditEventRecorded;
    private long failures;
    private long lastFailureTicks;
    public long FailedWrites => Interlocked.Read(ref failures);
    public DateTimeOffset? LastFailureUtc => Interlocked.Read(ref lastFailureTicks) is var ticks && ticks != 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    public async Task RecordAsync(AuditEvent auditEvent)
    {
        // Preserve the audit contract: a storage failure must not alter an authorization decision.
        try
        {
            if (!Options.ShouldRecord(auditEvent.Category, auditEvent.Outcome)) return;
            OperationalDatabase.Key(auditEvent.Id, nameof(auditEvent.Id));
            var json = JsonSerializer.Serialize(auditEvent);
            await using var connection = await database.OpenAsync();
            await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync();
            await using var command = OperationalDatabase.Command(connection, """
                DECLARE @existing nvarchar(max)=(SELECT Payload FROM fabrOps.Audit WITH(UPDLOCK,HOLDLOCK) WHERE Id=@id);
                IF @existing IS NOT NULL
                BEGIN
                    IF @existing COLLATE Latin1_General_100_BIN2 <> @json COLLATE Latin1_General_100_BIN2
                        THROW 51000, 'Audit records are immutable.', 1;
                    SELECT 0; RETURN;
                END
                INSERT fabrOps.Audit(Id,Timestamp,Category,Outcome,SubjectPrincipal,ResourcePrincipal,TraceId,Payload)
                VALUES(@id,@time,@category,@outcome,@subject,@resource,@trace,@json);
                SELECT 1;
                """, transaction, ("@id", auditEvent.Id), ("@time", auditEvent.Timestamp), ("@category", (int)auditEvent.Category),
                ("@outcome", (int)auditEvent.Outcome), ("@subject", auditEvent.SubjectPrincipal), ("@resource", auditEvent.ResourcePrincipal), ("@trace", auditEvent.TraceId), ("@json", json));
            var inserted = Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
            await transaction.CommitAsync();
            if (!inserted) return;
            var captured = JsonSerializer.Deserialize<AuditEvent>(json)!;
            foreach (var handler in OnAuditEventRecorded?.GetInvocationList() ?? [])
                try { ((Action<AuditEvent>)handler)(captured); }
                catch (Exception ex) { logger.LogWarning("Audit notification subscriber failed ({Type}).", ex.GetType().Name); }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failures); Interlocked.Exchange(ref lastFailureTicks, DateTimeOffset.UtcNow.Ticks);
            logger.LogError("Security audit persistence failed ({Type}). Failed writes: {Count}. No in-memory fallback was used.", ex.GetType().Name, FailedWrites);
        }
    }

    public async Task<List<AuditEvent>> GetEventsAsync(AuditQuery? query = null)
    {
        query ??= new();
        var limit = query.Limit ?? 100;
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query), "Audit page size must be 1–1000.");
        if ((query.Before is null) != (query.BeforeId is null)) throw new ArgumentException("Before and BeforeId must be supplied together.");
        await using var connection = await database.OpenAsync();
        await using var command = OperationalDatabase.Command(connection, """
            SELECT TOP(@limit) Payload FROM fabrOps.Audit
            WHERE (@category IS NULL OR Category=@category) AND (@outcome IS NULL OR Outcome=@outcome)
            AND (@subject IS NULL OR SubjectPrincipal=@subject) AND (@resource IS NULL OR ResourcePrincipal=@resource)
            AND (@trace IS NULL OR TraceId=@trace) AND (@since IS NULL OR Timestamp>=@since)
            AND (@before IS NULL OR Timestamp<@before OR (Timestamp=@before AND Id<@beforeId))
            ORDER BY Timestamp DESC, Id DESC;
            """, null, ("@limit", limit), ("@category", query.Category is { } c ? (int)c : null), ("@outcome", query.Outcome is { } o ? (int)o : null),
            ("@subject", string.IsNullOrEmpty(query.SubjectPrincipal) ? null : query.SubjectPrincipal), ("@resource", query.ResourcePrincipal),
            ("@trace", query.TraceId), ("@since", query.Since), ("@before", query.Before), ("@beforeId", query.BeforeId));
        var result = new List<AuditEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(JsonSerializer.Deserialize<AuditEvent>(reader.GetString(0))!);
        return result;
    }

    public async Task ClearAsync()
    {
        await using var connection = await database.OpenAsync();
        await using var command = OperationalDatabase.Command(connection, "DELETE FROM fabrOps.Audit;");
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Explicit bounded retention; never evicts records merely because a process buffer is full.</summary>
    public async Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, int batchSize = 1000, CancellationToken ct = default)
    {
        if (batchSize is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = await database.OpenAsync(ct);
        await using var command = OperationalDatabase.Command(connection, "DELETE TOP(@batch) FROM fabrOps.Audit WHERE Timestamp<@cutoff;", null, ("@batch", batchSize), ("@cutoff", cutoff));
        return await command.ExecuteNonQueryAsync(ct);
    }
}
