using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Audit;

/// <summary>
/// SQL-backed <see cref="IMemoryAuditLog"/> writing to <c>mem.MemoryAuditLog</c>.
/// Best-effort: insert failures are logged and swallowed.
/// </summary>
internal sealed class MemoryAuditLog : IMemoryAuditLog
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemoryAuditLog> _logger;
    private readonly string _connectionStringName;

    public MemoryAuditLog(
        IConfiguration configuration,
        ILogger<MemoryAuditLog> logger,
        string connectionStringName)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionStringName = connectionStringName;
    }

    public Task RecordAsync(
        string actionType, string scopeKey, Guid? memoryId = null, string? summary = null,
        string? actorId = null, string? payload = null, long? durationMs = null,
        CancellationToken ct = default)
        => RecordAsync(new MemoryAuditEntry
        {
            ActionType = actionType,
            ScopeKey = scopeKey,
            MemoryId = memoryId,
            Summary = summary,
            ActorId = actorId,
            Payload = payload,
            DurationMs = durationMs
        }, ct);

    public async Task RecordAsync(MemoryAuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            var connectionString = _configuration.GetConnectionString(_connectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                return;

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);

            const string sql = """
                INSERT INTO mem.MemoryAuditLog
                    (ActionType, ScopeKey, MemoryId, ActorId, ActorName, Summary, Payload, DurationMs)
                VALUES
                    (@actionType, @scopeKey, @memoryId, @actorId, @actorName, @summary, @payload, @durationMs);
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@actionType", entry.ActionType);
            command.Parameters.AddWithValue("@scopeKey", entry.ScopeKey);
            command.Parameters.AddWithValue("@memoryId", (object?)entry.MemoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("@actorId", (object?)entry.ActorId ?? DBNull.Value);
            command.Parameters.AddWithValue("@actorName", (object?)entry.ActorName ?? DBNull.Value);
            command.Parameters.AddWithValue("@summary", (object?)Truncate(entry.Summary, 500) ?? DBNull.Value);
            command.Parameters.AddWithValue("@payload", (object?)entry.Payload ?? DBNull.Value);
            command.Parameters.AddWithValue("@durationMs", (object?)entry.DurationMs ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Memory audit write failed for {ActionType} in scope '{ScopeKey}' — action itself was not affected.",
                entry.ActionType, entry.ScopeKey);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
