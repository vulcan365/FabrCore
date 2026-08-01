using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Services.Memory.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.Memory.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class SqlMemoryStoreIntegrationTests
{
    private DatabaseFixture _database = null!;
    private string _scope = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _database = await DatabaseFixture.CreateAsync();
        _scope = _database.CreateScopeKey("store");
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [TestMethod]
    public async Task SchemaInitialization_IsIdempotentAndCreatesVectorGraphSchema()
    {
        await MemorySchemaInitializer.EnsureSchemaAsync(
            _database.ConnectionString,
            TestEnvironment.EmbeddingDimensions,
            NullLogger.Instance);

        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
                 WHERE s.name = 'mem' AND t.name IN
                    ('MemoryEntity','MemoryChunk','MemoryRelationship','MemorySummaryNode','MemoryScope','MemoryAuditLog')),
                (SELECT vector_dimensions FROM sys.columns
                 WHERE object_id = OBJECT_ID('mem.MemoryChunk') AND name = 'Embedding'),
                (SELECT is_node FROM sys.tables WHERE object_id = OBJECT_ID('mem.MemoryEntity')),
                (SELECT is_edge FROM sys.tables WHERE object_id = OBJECT_ID('mem.MemoryRelationship'));
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(6, reader.GetInt32(0));
        Assert.AreEqual(TestEnvironment.EmbeddingDimensions, reader.GetInt32(1));
        Assert.IsTrue(reader.GetBoolean(2));
        Assert.IsTrue(reader.GetBoolean(3));
    }

    [TestMethod]
    public async Task EntityAndChunk_CrudRoundTripsMetadataContentAndTypeChanges()
    {
        var entry = await InsertMemoryAsync(
            _scope,
            "Original fact",
            MemoryType.Fact,
            "Original content",
            UnitVector(0),
            new Dictionary<string, string> { ["source"] = "integration-test" });

        var loaded = await _database.Store.GetEntityByIdAsync(_scope, entry.Id);
        var chunk = await _database.Store.GetPrimaryChunkAsync(_scope, entry.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(MemoryType.Fact, loaded.Type);
        Assert.AreEqual("integration-test", loaded.Metadata!["source"]);
        Assert.AreEqual("4", loaded.Metadata["__memoryVersion"]);
        Assert.IsNotNull(chunk);
        Assert.AreEqual("Original content", chunk.Content);

        loaded.Title = "Updated rule";
        loaded.Type = MemoryType.Rule;
        loaded.Description = "Updated description";
        loaded.Temperature = MemoryTemperature.Cold;
        loaded.IsPointInTime = true;
        await _database.Store.UpdateEntityAsync(_scope, loaded);
        chunk.Content = "Updated content";
        chunk.Embedding = UnitVector(1);
        await _database.Store.UpdateChunkAsync(_scope, chunk);

        var updated = await _database.Store.GetEntityByIdAsync(_scope, entry.Id);
        var updatedChunk = await _database.Store.GetPrimaryChunkAsync(_scope, entry.Id);
        Assert.IsNotNull(updated);
        Assert.AreEqual("Updated rule", updated.Title);
        Assert.AreEqual(MemoryType.Rule, updated.Type);
        Assert.AreEqual(MemoryTemperature.Cold, updated.Temperature);
        Assert.IsTrue(updated.IsPointInTime);
        Assert.AreEqual("Updated content", updatedChunk!.Content);

        Assert.IsTrue(await _database.Store.DeleteEntityAsync(_scope, entry.Id));
        Assert.IsFalse(await _database.Store.DeleteEntityAsync(_scope, entry.Id));
        Assert.IsNull(await _database.Store.GetPrimaryChunkAsync(_scope, entry.Id));
    }

    [TestMethod]
    public async Task VectorSearch_RanksBySimilarityFiltersTypeAndIsolatesScope()
    {
        var exact = await InsertMemoryAsync(
            _scope, "Exact match", MemoryType.Fact, "alpha", UnitVector(0));
        var orthogonal = await InsertMemoryAsync(
            _scope, "Orthogonal rule", MemoryType.Rule, "beta", UnitVector(1));
        var otherScope = _database.CreateScopeKey("other");
        await InsertMemoryAsync(
            otherScope, "Other scope exact match", MemoryType.Fact, "secret", UnitVector(0));

        var results = await _database.Store.VectorSearchAsync(_scope, UnitVector(0), 10);
        var rules = await _database.Store.VectorSearchAsync(
            _scope, UnitVector(0), 10, MemoryType.Rule);

        Assert.HasCount(2, results);
        Assert.AreEqual(exact.Id, results[0].Entry.Id);
        Assert.AreEqual(0d, results[0].Distance, 0.000001);
        Assert.AreEqual(orthogonal.Id, results[1].Entry.Id);
        Assert.IsTrue(results.All(r => r.Entry.ScopeKey == _scope));
        Assert.HasCount(1, rules);
        Assert.AreEqual(orthogonal.Id, rules[0].Entry.Id);
    }

    [TestMethod]
    public async Task Relationships_AreScopedTraversableAndDeletedWithEntity()
    {
        var from = await InsertMemoryAsync(
            _scope, "Customer onboarding", MemoryType.Procedural, "steps", UnitVector(2));
        var to = await InsertMemoryAsync(
            _scope, "Identity policy", MemoryType.Rule, "verify identity", UnitVector(3));

        await _database.Store.InsertRelationshipAsync(
            _scope, from.Id, to.Id, "requires", "Onboarding requires identity verification", 0.9);

        var outgoing = await _database.Store.GetRelationshipsAsync(_scope, from.Id);
        var incoming = await _database.Store.GetRelationshipsAsync(_scope, to.Id);
        Assert.HasCount(1, outgoing);
        Assert.AreEqual(to.Id, outgoing[0].RelatedEntityId);
        Assert.AreEqual("requires", outgoing[0].RelationshipType);
        Assert.HasCount(1, incoming);
        Assert.AreEqual(from.Id, incoming[0].RelatedEntityId);

        Assert.IsTrue(await _database.Store.DeleteEntityAsync(_scope, from.Id));
        Assert.IsEmpty(await _database.Store.GetRelationshipsAsync(_scope, to.Id));
    }

    [TestMethod]
    public async Task HotIndex_ConcurrentWritersDoNotLoseEntries()
    {
        _database.Options.HotIndex.MaxEntries = 100;
        _database.Options.HotIndex.MaxTokens = 100_000;
        var manager = new MemoryIndexManager(
            _database.Store, _database.Options, NullLoggerFactory.Instance);
        var baseline = DateTime.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            manager.AddIndexEntryAsync(_scope, new MemoryIndexEntry
            {
                MemoryId = Guid.NewGuid(),
                Title = $"Concurrent memory {i}",
                Type = MemoryType.Fact,
                DescriptionHook = $"hook {i}",
                UpdatedAt = baseline.AddMilliseconds(i)
            })));

        var index = await manager.GetIndexAsync(_scope);
        Assert.HasCount(24, index.Entries);
        Assert.AreEqual(24, index.Entries.Select(e => e.MemoryId).Distinct().Count());
    }

    [TestMethod]
    public async Task ScopeRegistry_CreateEnsureCountAndAuditRoundTrip()
    {
        var scope = await _database.ScopeService.CreateScopeAsync(
            _scope, "Shared testing scope", true, "test-runner");
        await InsertMemoryAsync(_scope, "Fact", MemoryType.Fact, "content", UnitVector(4));

        Assert.IsTrue(scope.IsShared);
        Assert.IsTrue(await _database.ScopeService.ScopeExistsAsync(_scope));
        Assert.AreEqual(1, await _database.ScopeService.CountMemoriesInScopeAsync(_scope));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _database.ScopeService.CreateScopeAsync(_scope, null));

        await _database.ScopeService.EnsureScopeAsync(_scope);
        var loaded = await _database.ScopeService.GetScopeAsync(_scope);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("Shared testing scope", loaded.Description);

        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM mem.MemoryAuditLog WHERE ScopeKey = @scopeKey AND ActionType = 'ScopeCreated'",
            connection);
        command.Parameters.AddWithValue("@scopeKey", _scope);
        Assert.AreEqual(1, (int)(await command.ExecuteScalarAsync() ?? 0));
    }

    private async Task<MemoryEntry> InsertMemoryAsync(
        string scope,
        string title,
        MemoryType type,
        string content,
        float[] embedding,
        Dictionary<string, string>? metadata = null)
    {
        var entry = await _database.Store.InsertEntityAsync(scope, new MemoryEntry
        {
            Title = title,
            Type = type,
            Temperature = MemoryTemperature.Warm,
            Description = title,
            Metadata = metadata
        });
        await _database.Store.InsertChunkAsync(scope, new MemoryChunkEntry
        {
            EntityId = entry.Id,
            Content = content,
            Embedding = embedding,
            ChunkIndex = 0
        });
        return entry;
    }

    private static float[] UnitVector(int index)
    {
        var vector = new float[TestEnvironment.EmbeddingDimensions];
        vector[index] = 1f;
        return vector;
    }
}
