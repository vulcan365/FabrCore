using System.Text.Json;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// SQL-backed implementation of <see cref="IKnowledgeScopeService"/>.
/// Owns every statement that touches <c>grag.KnowledgeScope</c>. Plugins
/// and agents never hit the table directly.
/// </summary>
public sealed class KnowledgeScopeService : IKnowledgeScopeService
{
    private readonly string _connectionString;
    private readonly ILogger<KnowledgeScopeService> _logger;
    private readonly IGraphRagAuditLog _audit;

    public KnowledgeScopeService(
        IConfiguration configuration,
        ILogger<KnowledgeScopeService> logger,
        string connectionStringName,
        IGraphRagAuditLog audit)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrWhiteSpace(connectionStringName))
            throw new ArgumentException("Connection string name is required", nameof(connectionStringName));

        _connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' not found in configuration");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<KnowledgeScope> CreateScopeAsync(
        string scopeKey,
        string description,
        double defaultPriority = 1.0,
        string? metadata = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("ScopeKey is required", nameof(scopeKey));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            INSERT INTO {GraphRagSchemaInitializer.SchemaName}.KnowledgeScope
                (ScopeKey, Description, DefaultPriority, Metadata)
            OUTPUT INSERTED.ScopeKey, INSERTED.Description, INSERTED.DefaultPriority,
                   INSERTED.Metadata, INSERTED.CreatedAt
            VALUES (@scopeKey, @description, @defaultPriority, @metadata);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@defaultPriority", defaultPriority);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("CreateScope INSERT did not return a row");

        var scope = ReadScope(reader);
        _logger.LogInformation("Created scope '{ScopeKey}' with default priority {Priority}",
            scope.ScopeKey, scope.DefaultPriority);

        await _audit.RecordScopeCreatedAsync(
            scopeKey: scope.ScopeKey,
            description: scope.Description,
            defaultPriority: scope.DefaultPriority,
            ct: ct);

        return scope;
    }

    public async Task<KnowledgeScope?> GetScopeAsync(string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            return null;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT ScopeKey, Description, DefaultPriority, Metadata, CreatedAt
            FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeScope
            WHERE ScopeKey = @scopeKey;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadScope(reader) : null;
    }

    public async Task<IReadOnlyList<KnowledgeScope>> ListScopesAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT ScopeKey, Description, DefaultPriority, Metadata, CreatedAt
            FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeScope
            ORDER BY ScopeKey;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var scopes = new List<KnowledgeScope>();
        while (await reader.ReadAsync(ct))
            scopes.Add(ReadScope(reader));
        return scopes;
    }

    public async Task<bool> ScopeExistsAsync(string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            return false;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT COUNT(*)
            FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeScope
            WHERE ScopeKey = @scopeKey;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        return (int)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    public async Task<int> CountEntitiesInScopeAsync(string scopeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            return 0;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = $"""
            SELECT COUNT(*)
            FROM {GraphRagSchemaInitializer.SchemaName}.KnowledgeEntity
            WHERE ScopeKey = @scopeKey;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static KnowledgeScope ReadScope(SqlDataReader reader) => new()
    {
        ScopeKey = reader.GetString(reader.GetOrdinal("ScopeKey")),
        Description = reader.IsDBNull(reader.GetOrdinal("Description"))
            ? null
            : reader.GetString(reader.GetOrdinal("Description")),
        DefaultPriority = reader.GetDouble(reader.GetOrdinal("DefaultPriority")),
        Metadata = reader.IsDBNull(reader.GetOrdinal("Metadata"))
            ? null
            : reader.GetString(reader.GetOrdinal("Metadata")),
        CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
    };
}
