using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Services;
using FabrCore.Sdk;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.Memory.Tests.Infrastructure;

internal sealed class DatabaseFixture : IAsyncDisposable
{
    private readonly HashSet<string> _scopes = [];

    public DatabaseFixture(string connectionString, IEmbeddings? embeddings = null)
    {
        ConnectionString = connectionString;
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{TestEnvironment.ConnectionStringName}"] = connectionString
            })
            .Build();

        Options = new AgentMemoryOptions
        {
            ConnectionStringName = TestEnvironment.ConnectionStringName,
            EmbeddingDimensions = TestEnvironment.EmbeddingDimensions
        };

        Store = new SqlMemoryStore(Options, Configuration, NullLoggerFactory.Instance, embeddings);
        AuditLog = new MemoryAuditLog(
            Configuration,
            NullLogger<MemoryAuditLog>.Instance,
            TestEnvironment.ConnectionStringName);
        ScopeService = new MemoryScopeService(
            Configuration,
            Options,
            AuditLog,
            NullLogger<MemoryScopeService>.Instance);
    }

    public string ConnectionString { get; }
    public IConfiguration Configuration { get; }
    public AgentMemoryOptions Options { get; }
    public SqlMemoryStore Store { get; }
    public IMemoryAuditLog AuditLog { get; }
    public MemoryScopeService ScopeService { get; }

    public static async Task<DatabaseFixture> CreateAsync(IEmbeddings? embeddings = null)
    {
        var connectionString = TestEnvironment.RequireDatabaseConnectionString();
        await MemorySchemaInitializer.EnsureSchemaAsync(
            connectionString,
            TestEnvironment.EmbeddingDimensions,
            NullLogger.Instance);
        return new DatabaseFixture(connectionString, embeddings);
    }

    public string CreateScopeKey(string prefix)
    {
        var scope = TestEnvironment.NewScope(prefix);
        _scopes.Add(scope);
        return scope;
    }

    public void TrackScope(string scopeKey) => _scopes.Add(scopeKey);

    public async Task DeleteScopeAsync(string scopeKey)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        foreach (var sql in new[]
                 {
                     "DELETE FROM mem.MemoryRelationship WHERE ScopeKey = @scopeKey",
                     "DELETE FROM mem.MemoryChunk WHERE ScopeKey = @scopeKey",
                     "DELETE FROM mem.MemorySummaryNode WHERE ScopeKey = @scopeKey",
                     "DELETE FROM mem.MemoryEntity WHERE ScopeKey = @scopeKey",
                     "DELETE FROM mem.MemoryScope WHERE ScopeKey = @scopeKey",
                     "DELETE FROM mem.MemoryAuditLog WHERE ScopeKey = @scopeKey"
                 })
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@scopeKey", scopeKey);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        _scopes.Remove(scopeKey);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes.ToArray())
            await DeleteScopeAsync(scope);
    }
}
