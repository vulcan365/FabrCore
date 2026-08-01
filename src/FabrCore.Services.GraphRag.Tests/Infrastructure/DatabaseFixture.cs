using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.GraphRag.Tests.Infrastructure;

internal sealed class DatabaseFixture : IAsyncDisposable
{
    private readonly HashSet<string> _scopes = [];

    private DatabaseFixture(string connectionString)
    {
        ConnectionString = connectionString;
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{TestEnvironment.ConnectionStringName}"] = connectionString
            })
            .Build();
        Audit = new GraphRagAuditLog(Configuration, NullLogger<GraphRagAuditLog>.Instance, TestEnvironment.ConnectionStringName);
        Embeddings = new DeterministicEmbeddings();
        Scopes = new KnowledgeScopeService(Configuration, NullLogger<KnowledgeScopeService>.Instance, TestEnvironment.ConnectionStringName, Audit);
        Ingestion = new KnowledgeIngestionService(Configuration, NullLogger<KnowledgeIngestionService>.Instance, TestEnvironment.ConnectionStringName, Audit, Embeddings);
        Search = new KnowledgeSearchService(Configuration, NullLogger<KnowledgeSearchService>.Instance, TestEnvironment.ConnectionStringName, Audit, Embeddings);
    }

    public string ConnectionString { get; }
    public IConfiguration Configuration { get; }
    public DeterministicEmbeddings Embeddings { get; }
    public IGraphRagAuditLog Audit { get; }
    public IKnowledgeScopeService Scopes { get; }
    public IKnowledgeIngestionService Ingestion { get; }
    public IKnowledgeSearchService Search { get; }

    public static async Task<DatabaseFixture> CreateAsync()
    {
        var connectionString = TestEnvironment.RequireDatabaseConnectionString();
        await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, NullLogger.Instance);
        return new DatabaseFixture(connectionString);
    }

    public string CreateScopeKey(string prefix)
    {
        var scope = TestEnvironment.NewScope(prefix);
        _scopes.Add(scope);
        return scope;
    }

    public async Task<string> CreateScopeAsync(string prefix)
    {
        var scope = CreateScopeKey(prefix);
        await Scopes.CreateScopeAsync(scope, $"Isolated GraphRAG test scope for {prefix}");
        return scope;
    }

    public async Task DeleteScopeAsync(string scope)
    {
        var documentIds = new List<Guid>();
        await using (var lookup = new SqlConnection(ConnectionString))
        {
            await lookup.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT DocumentId FROM grag.SourceDocument WHERE ScopeKey = @scope", lookup);
            command.Parameters.AddWithValue("@scope", scope);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                documentIds.Add(reader.GetGuid(0));
        }

        // Exercise the public provenance-aware cleanup path first so shared
        // entities survive and document-created taxonomy can be swept safely.
        foreach (var documentId in documentIds)
            await Ingestion.DeleteDocumentAsync(documentId);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var sql in new[]
                 {
                     "DELETE FROM grag.KnowledgeChunk WHERE ScopeKey = @scope",
                     "DELETE FROM grag.BelongsTo WHERE ScopeKey = @scope",
                     "DELETE FROM grag.KnowledgeRelationship WHERE ScopeKey = @scope",
                     "DELETE FROM grag.CommunitySummary WHERE ScopeKey = @scope",
                     "DELETE FROM grag.KnowledgeEntity WHERE ScopeKey = @scope",
                     "DELETE FROM grag.SourceDocument WHERE ScopeKey = @scope",
                     "DELETE FROM grag.KnowledgeScope WHERE ScopeKey = @scope",
                     "DELETE FROM grag.ActionAudit WHERE ScopeKey = @scope"
                 })
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@scope", scope);
            await command.ExecuteNonQueryAsync();
        }

        _scopes.Remove(scope);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes.ToArray())
            await DeleteScopeAsync(scope);
    }
}
