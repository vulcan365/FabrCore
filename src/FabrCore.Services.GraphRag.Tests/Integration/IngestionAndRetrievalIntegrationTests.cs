using System.Text.Json;
using FabrCore.Core;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.Tests.Infrastructure;
using FabrCore.Sdk;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.GraphRag.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class IngestionAndRetrievalIntegrationTests
{
    [TestMethod]
    public async Task IngestDocument_StoresChunksAndIdenticalReingestIsReused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var scope = await fixture.CreateScopeAsync("ingest-reuse");
        const string content = "# Apollo runbook\n\nThe Apollo database migration uses a blue-green cutover.";

        var first = await fixture.Ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest("apollo.md", scope, content));
        var second = await fixture.Ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest("apollo.md", scope, content));

        Assert.AreEqual("Completed", first.Status);
        Assert.IsGreaterThan(0, first.ChunkCount);
        Assert.AreEqual(first.DocumentId, second.DocumentId);
        Assert.IsTrue(second.Reused);
        Assert.AreEqual(1, second.VersionNumber);

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM grag.SourceDocument WHERE ScopeKey = @scope;
            SELECT COUNT(*) FROM grag.KnowledgeChunk WHERE ScopeKey = @scope;
            """, connection);
        command.Parameters.AddWithValue("@scope", scope);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.IsTrue(await reader.NextResultAsync());
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(first.ChunkCount, reader.GetInt32(0));
    }

    [TestMethod]
    public async Task SearchChunks_RanksRelevantContentAndNeverLeaksAnotherScope()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var allowed = await fixture.CreateScopeAsync("retrieval-allowed");
        var denied = await fixture.CreateScopeAsync("retrieval-denied");

        await fixture.Ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
            "apollo.md", allowed, "# Apollo\n\nApollo uses a database blue-green cutover on Thursday."));
        await fixture.Ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
            "orion.md", allowed, "# Orion\n\nOrion rotates security certificates every Monday."));
        await fixture.Ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
            "secret-apollo.md", denied, "# Classified Apollo\n\nApollo secret launch code is ZEPHYR-99."));

        var json = await fixture.Search.SearchChunksAsync(new ScopedSearchRequest("Apollo database", [allowed], 5));
        using var results = JsonDocument.Parse(json);
        var rows = results.RootElement.EnumerateArray().ToArray();

        Assert.IsGreaterThanOrEqualTo(1, rows.Length);
        Assert.AreEqual(allowed, rows[0].GetProperty("scope").GetString());
        StringAssert.Contains(rows[0].GetProperty("content").GetString()!, "blue-green cutover");
        Assert.IsTrue(rows.All(r => r.GetProperty("scope").GetString() == allowed));
        Assert.IsFalse(json.Contains("ZEPHYR-99", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SearchEntities_EntityTypeFilterAndAuditAreApplied()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var scope = await fixture.CreateScopeAsync("entity-search");
        await fixture.Ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
            "apollo.md", scope, "# Apollo\n\nApollo database deployment guide."));

        var found = await fixture.Search.SearchEntitiesAsync(new ScopedSearchRequest("Apollo", [scope], 10, "Document"));
        var filteredOut = await fixture.Search.SearchEntitiesAsync(new ScopedSearchRequest("Apollo", [scope], 10, "Person"));

        StringAssert.Contains(found, "apollo.md");
        Assert.AreEqual("No matching entities found.", filteredOut);

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM grag.ActionAudit
            WHERE ScopeKey = @scope AND ActionType = 'SearchExecuted';
            """, connection);
        command.Parameters.AddWithValue("@scope", scope);
        Assert.AreEqual(2, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task IngestDocument_BatchedGraphWritesPreserveEntitiesRelationshipsAndContributions()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var scope = await fixture.CreateScopeAsync("batched-graph-writes");
        var provider = new ServiceCollection()
            .AddSingleton<IFabrCoreChatClientService>(new DeterministicChatClientService())
            .BuildServiceProvider();
        var ingestion = new KnowledgeIngestionService(
            fixture.Configuration,
            NullLogger<KnowledgeIngestionService>.Instance,
            TestEnvironment.ConnectionStringName,
            fixture.Audit,
            fixture.Embeddings,
            serviceProvider: provider);

        var result = await ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
            "batched.md",
            scope,
            "# Apollo\n\nApollo depends on the primary database for deployment state."));

        Assert.AreEqual("Completed", result.Status);
        Assert.AreEqual(2, result.ExtractedEntityCount);
        Assert.AreEqual(3, result.ExtractedRelationshipCount);

        var contributions = await ingestion.GetContributionsAsync(result.DocumentId);
        Assert.IsGreaterThanOrEqualTo(10, contributions.Count);
        Assert.AreEqual(2, contributions.Count(item => item.Kind == ContributionKind.Entity));
        Assert.AreEqual(2, contributions.Count(item => item.Kind == ContributionKind.ExtractedFromEdge));
        Assert.AreEqual(1, contributions.Count(item => item.Kind == ContributionKind.Relationship));

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT ResolvedModelName, ResolvedProviderName, ResolvedDeploymentModelName,
                   ChatCallCount, ExtractionBatchCount, ExtractionRetryCount, ExtractionTruncationCount,
                   EmbeddingBatchCount, SqlCommandBatchCount,
                   ChunkEmbeddingMs, LlmExtractionMs, EntityEmbeddingMs, SqlWriteMs
            FROM grag.IngestionMetric
            WHERE DocumentId = @documentId;

            SELECT TOP(1) Payload
            FROM grag.ActionAudit
            WHERE SubjectId = CONVERT(NVARCHAR(36), @documentId)
              AND ActionType = 'DocumentIngested'
            ORDER BY OccurredAt DESC;
            """, connection);
        command.Parameters.AddWithValue("@documentId", result.DocumentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual("graphrag", reader.GetString(0));
        Assert.AreEqual("Test", reader.GetString(1));
        Assert.AreEqual("deterministic", reader.GetString(2));
        Assert.AreEqual(1, reader.GetInt32(3));
        Assert.AreEqual(1, reader.GetInt32(4));
        Assert.AreEqual(0, reader.GetInt32(5));
        Assert.AreEqual(0, reader.GetInt32(6));
        Assert.AreEqual(2, reader.GetInt32(7));
        Assert.IsGreaterThanOrEqualTo(4, reader.GetInt32(8));
        Assert.IsGreaterThanOrEqualTo(0L, reader.GetInt64(9));
        Assert.IsGreaterThanOrEqualTo(0L, reader.GetInt64(10));
        Assert.IsGreaterThanOrEqualTo(0L, reader.GetInt64(11));
        Assert.IsGreaterThanOrEqualTo(0L, reader.GetInt64(12));

        Assert.IsTrue(await reader.NextResultAsync());
        Assert.IsTrue(await reader.ReadAsync());
        using var auditPayload = JsonDocument.Parse(reader.GetString(0));
        Assert.AreEqual("graphrag", auditPayload.RootElement.GetProperty("resolvedModelName").GetString());
        Assert.AreEqual("Test", auditPayload.RootElement.GetProperty("resolvedProviderName").GetString());
        Assert.AreEqual("deterministic", auditPayload.RootElement.GetProperty("resolvedDeploymentModelName").GetString());
        Assert.AreEqual(1, auditPayload.RootElement.GetProperty("chatCallCount").GetInt32());
        Assert.AreEqual(1, auditPayload.RootElement.GetProperty("extractionBatchCount").GetInt32());
        Assert.AreEqual(0, auditPayload.RootElement.GetProperty("extractionRetryCount").GetInt32());
        Assert.AreEqual(0, auditPayload.RootElement.GetProperty("extractionTruncationCount").GetInt32());
        Assert.AreEqual(2, auditPayload.RootElement.GetProperty("embeddingBatchCount").GetInt32());

        var admin = new GraphRagAdminService(
            fixture.Configuration,
            new GraphRagOptions { ConnectionStringName = TestEnvironment.ConnectionStringName },
            provider,
            NullLogger<GraphRagAdminService>.Instance);
        var metrics = await admin.GetMetricsSummaryAsync(scope, since: null);
        Assert.AreEqual(1, metrics.TotalIngestionRuns);
        Assert.AreEqual(1, metrics.TotalChatCalls);
        Assert.AreEqual(2, metrics.TotalEmbeddingBatches);
        Assert.AreEqual(1, metrics.TotalExtractionBatches);
        Assert.AreEqual(0, metrics.TotalExtractionRetries);
        Assert.AreEqual(0, metrics.TotalExtractionTruncations);
        Assert.IsGreaterThanOrEqualTo(0L, metrics.TotalDurationMs);
        Assert.HasCount(1, metrics.TopDocuments);
    }

    [TestMethod]
    public async Task IngestDocument_ConcurrentDocumentsCompleteWithSharedTaxonomyAndEntities()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var scope = await fixture.CreateScopeAsync("concurrent-batched-ingestion");
        var provider = new ServiceCollection()
            .AddSingleton<IFabrCoreChatClientService>(new DeterministicChatClientService())
            .BuildServiceProvider();
        var ingestion = new KnowledgeIngestionService(
            fixture.Configuration,
            NullLogger<KnowledgeIngestionService>.Instance,
            TestEnvironment.ConnectionStringName,
            fixture.Audit,
            fixture.Embeddings,
            serviceProvider: provider);

        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(index =>
            ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
                $"concurrent-{index}.md",
                scope,
                $"# Apollo {index}\n\nApollo depends on the primary database for deployment state."))));

        Assert.IsTrue(results.All(result => result.Status == "Completed"));
        Assert.IsTrue(results.All(result => result.ExtractedEntityCount == 2));
        Assert.IsTrue(results.All(result => result.ExtractedRelationshipCount == 3));
    }

    private sealed class DeterministicChatClientService : IFabrCoreChatClientService
    {
        private readonly IChatClient _client = new DeterministicChatClient();

        public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
            => string.Equals(name, "graphrag", StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(_client)
                : throw new InvalidOperationException($"Model '{name}' is unavailable.");

#pragma warning disable MEAI001
        public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
            => throw new NotSupportedException();
#pragma warning restore MEAI001

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
            => throw new NotSupportedException();

        public Task<ModelConfiguration> GetModelConfigurationAsync(string name)
            => string.Equals(name, "graphrag", StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(new ModelConfiguration
                {
                    Name = "graphrag",
                    Provider = "Test",
                    Uri = "https://test.invalid",
                    Model = "deterministic",
                    ApiKeyAlias = "test",
                    TimeoutSeconds = 30,
                    ContextWindowTokens = 128_000
                })
                : throw new InvalidOperationException($"Model '{name}' is unavailable.");
    }

    private sealed class DeterministicChatClient : IChatClient
    {
        private const string Response = """
            {
              "domain": { "name": "Engineering", "description": "Engineering knowledge", "isNew": true, "confidence": 0.95 },
              "category": { "name": "Deployments", "description": "Deployment knowledge", "isNew": true, "confidence": 0.95 },
              "entities": [
                { "name": "Apollo", "entityType": "System", "description": "Deployment system" },
                { "name": "Primary Database", "entityType": "Database", "description": "Deployment state database" }
              ],
              "relationships": [
                { "from": "Apollo", "fromType": "System", "to": "Primary Database", "toType": "Database", "type": "DEPENDS_ON", "description": "Stores deployment state", "confidence": 0.9 }
              ]
            }
            """;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, Response);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
