using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// SQL-backed scope registry over <c>mem.MemoryScope</c>.
/// </summary>
internal sealed class MemoryScopeService : IMemoryScopeService
{
    private readonly IConfiguration _configuration;
    private readonly AgentMemoryOptions _options;
    private readonly IMemoryAuditLog _auditLog;
    private readonly ILogger<MemoryScopeService> _logger;

    public MemoryScopeService(
        IConfiguration configuration,
        AgentMemoryOptions options,
        IMemoryAuditLog auditLog,
        ILogger<MemoryScopeService> logger)
    {
        _configuration = configuration;
        _options = options;
        _auditLog = auditLog;
        _logger = logger;
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = _configuration.GetConnectionString(_options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{_options.ConnectionStringName}' not found in configuration.");
        return new SqlConnection(connectionString);
    }

    public async Task<MemoryScope> CreateScopeAsync(
        string scopeKey, string? description, bool isShared = true,
        string? createdBy = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        scopeKey = scopeKey.Trim();

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO mem.MemoryScope (ScopeKey, Description, IsShared, CreatedBy)
            OUTPUT INSERTED.ScopeKey, INSERTED.Description, INSERTED.IsShared, INSERTED.CreatedAt, INSERTED.CreatedBy
            SELECT @scopeKey, @description, @isShared, @createdBy
            WHERE NOT EXISTS (SELECT 1 FROM mem.MemoryScope WHERE ScopeKey = @scopeKey);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@scopeKey", scopeKey);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@isShared", isShared);
        command.Parameters.AddWithValue("@createdBy", (object?)createdBy ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Memory scope '{scopeKey}' already exists.");

        var scope = ReadScope(reader);
        await reader.DisposeAsync();

        await _auditLog.RecordAsync(
            "ScopeCreated", scopeKey,
            summary: description ?? (isShared ? "Shared scope created" : "Scope created"),
            actorId: createdBy, ct: ct);

        _logger.LogInformation("Memory scope '{ScopeKey}' created (shared: {IsShared}).", scopeKey, isShared);
        return scope;
    }

    public async Task EnsureScopeAsync(string scopeKey, bool isShared = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        scopeKey = scopeKey.Trim();

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            MERGE mem.MemoryScope AS target
            USING (SELECT @scopeKey AS ScopeKey) AS source
            ON target.ScopeKey = source.ScopeKey
            WHEN NOT MATCHED THEN
                INSERT (ScopeKey, IsShared, CreatedBy)
                VALUES (@scopeKey, @isShared, @scopeKey);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@scopeKey", scopeKey);
        command.Parameters.AddWithValue("@isShared", isShared);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<MemoryScope?> GetScopeAsync(string scopeKey, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT ScopeKey, Description, IsShared, CreatedAt, CreatedBy
            FROM mem.MemoryScope WHERE ScopeKey = @scopeKey;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@scopeKey", scopeKey);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadScope(reader) : null;
    }

    public async Task<IReadOnlyList<MemoryScope>> ListScopesAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT ScopeKey, Description, IsShared, CreatedAt, CreatedBy
            FROM mem.MemoryScope ORDER BY ScopeKey;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var scopes = new List<MemoryScope>();
        while (await reader.ReadAsync(ct))
            scopes.Add(ReadScope(reader));
        return scopes;
    }

    public async Task<bool> ScopeExistsAsync(string scopeKey, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = "SELECT 1 FROM mem.MemoryScope WHERE ScopeKey = @scopeKey;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@scopeKey", scopeKey);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<int> CountMemoriesInScopeAsync(string scopeKey, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT COUNT(*) FROM mem.MemoryEntity
            WHERE ScopeKey = @scopeKey AND Name <> @indexSentinel;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@scopeKey", scopeKey);
        command.Parameters.AddWithValue("@indexSentinel", SqlMemoryStore.IndexSentinelName);
        return (int)(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    private static MemoryScope ReadScope(SqlDataReader reader) => new()
    {
        ScopeKey = reader.GetString(0),
        Description = reader.IsDBNull(1) ? null : reader.GetString(1),
        IsShared = reader.GetBoolean(2),
        CreatedAt = reader.GetDateTime(3),
        CreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4)
    };
}
