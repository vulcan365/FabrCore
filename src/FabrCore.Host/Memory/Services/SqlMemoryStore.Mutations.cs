using Microsoft.Data.SqlClient;

namespace FabrCore.Services.Memory.Services;

internal partial class SqlMemoryStore
{
    private sealed record Mutation(string Scope, SqlConnection Connection, SqlTransaction Transaction)
    {
        public List<Func<Task>> AfterCommit { get; } = [];
        public bool KnowledgeChanged { get; set; }
    }
    private readonly AsyncLocal<Mutation?> mutation = new();
    internal bool IsMutationActive => mutation.Value is not null;
    private SqlTransaction? ActiveTransaction => mutation.Value?.Transaction;

    private void MarkKnowledgeChanged()
    {
        if (mutation.Value is { } active) active.KnowledgeChanged = true;
    }

    internal async Task<T> SerializeMaintenanceAsync<T>(string scope, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(deadline.Token);
        await using var transaction = connection.BeginTransaction();
        await AcquireMutationLockAsync(connection, transaction, scope, deadline.Token);
        // Maintenance owns its SQL operations and may rebuild summaries on separate connections.
        // This transaction owns only the application lock, not those writes.
        return await action(deadline.Token);
    }

    internal static async Task AcquireMutationLockAsync(SqlConnection connection, SqlTransaction transaction, string scope, CancellationToken ct)
    {
        await using var command = new SqlCommand("DECLARE @r int, @resource nvarchar(255) = CONCAT('mem-mutation-', CHECKSUM(@scope COLLATE DATABASE_DEFAULT)); EXEC @r = sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; SELECT @r;", connection, transaction);
        command.Parameters.AddWithValue("@scope", scope);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0)
            throw new TimeoutException("Could not acquire the shared memory mutation lock.");
    }

    internal Task AfterCommitAsync(Func<Task> action)
    {
        if (mutation.Value is not { } active) return action();
        active.AfterCommit.Add(action);
        return Task.CompletedTask;
    }

    internal async Task<Guid[]?> GetExtractionReceiptAsync(string scope, string hash, CancellationToken ct)
    {
        await using var lease = await OpenConnectionAsync(ct);
        await using var command = new SqlCommand($"SELECT EntityIds FROM {SchemaName}.MemoryExtractionReceipt WHERE ScopeKey=@scope AND SourceHash=@hash", lease.Connection, ActiveTransaction);
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@hash", hash);
        var result = await command.ExecuteScalarAsync(ct);
        return result is string json ? System.Text.Json.JsonSerializer.Deserialize<Guid[]>(json) : null;
    }

    internal async Task SaveExtractionReceiptAsync(string scope, string hash, IEnumerable<Guid> ids, CancellationToken ct)
    {
        if (!IsMutationActive) throw new InvalidOperationException("Extraction receipts require a memory transaction.");
        await using var lease = await OpenConnectionAsync(ct);
        await using var command = new SqlCommand($"INSERT INTO {SchemaName}.MemoryExtractionReceipt (ScopeKey,SourceHash,EntityIds) VALUES (@scope,@hash,@ids)", lease.Connection, ActiveTransaction);
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@ids", System.Text.Json.JsonSerializer.Serialize(ids));
        await command.ExecuteNonQueryAsync(ct);
    }

    // All facade mutations on a shared SQL scope serialize across provider instances/processes.
    // The finite deadline also bounds model work performed while a mutation is in progress.
    internal async Task<T> MutateAsync<T>(string scope, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (mutation.Value is { } nested)
        {
            if (nested.Scope != scope) throw new InvalidOperationException("A memory transaction cannot cross scopes.");
            return await action(ct);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(deadline.Token);
        await using var transaction = connection.BeginTransaction();
        // CHECKSUM uses database collation, matching ScopeKey equality (including case-insensitive
        // databases). A collision only serializes unrelated scopes; it cannot merge their data.
        await AcquireMutationLockAsync(connection, transaction, scope, deadline.Token);
        var state = new Mutation(scope, connection, transaction);
        mutation.Value = state;
        try
        {
            var result = await action(deadline.Token);
            if (state.KnowledgeChanged)
            {
                await using var invalidate = new SqlCommand($"DELETE FROM {SchemaName}.MemorySummaryNode WHERE ScopeKey=@scope", connection, transaction);
                invalidate.Parameters.AddWithValue("@scope", scope);
                await invalidate.ExecuteNonQueryAsync(deadline.Token);
            }
            deadline.Token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(deadline.Token);
            mutation.Value = null;
            foreach (var callback in state.AfterCommit) await callback();
            return result;
        }
        finally
        {
            // Disposal rolls back an uncommitted transaction, including cancellation/failure.
            mutation.Value = null;
        }
    }

    private sealed class ConnectionLease(SqlConnection connection, bool owns) : IAsyncDisposable
    {
        public SqlConnection Connection => connection;
        public ValueTask DisposeAsync() => owns ? connection.DisposeAsync() : ValueTask.CompletedTask;
    }

    private async Task<ConnectionLease> OpenConnectionAsync(CancellationToken ct)
    {
        if (mutation.Value is { } active) return new(active.Connection, false);
        var connection = new SqlConnection(_connectionString);
        try { await connection.OpenAsync(ct); return new(connection, true); }
        catch { await connection.DisposeAsync(); throw; }
    }
}
