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
    public async Task MultipleMatchedChunksAreRankedBoundedAndScopeFiltered()
    {
        var active = await InsertMemoryAsync(_scope, "multi", MemoryType.Fact, "overview", UnitVector(1));
        var expected = new List<Guid>();
        for (var i = 1; i <= 4; i++)
            expected.Add((await _database.Store.InsertChunkAsync(_scope, new MemoryChunkEntry {
                EntityId = active.Id, ChunkIndex = i, Content = new string('x', 1000), Embedding = UnitVector(0) })).ChunkId);
        var foreign = await InsertMemoryAsync(_database.CreateScopeKey("multi-other"), "other", MemoryType.Fact, "foreign", UnitVector(0));
        var cold = await InsertMemoryAsync(_scope, "multi-cold", MemoryType.Fact, "cold", UnitVector(0));
        cold.Temperature = MemoryTemperature.Cold;
        await _database.Store.UpdateEntityAsync(_scope, cold);
        var matches = await _database.Store.GetMatchedChunksAsync(_scope, UnitVector(0), [active.Id, foreign.Id, cold.Id], 3, 160);
        Assert.HasCount(1, matches);
        CollectionAssert.AreEqual(expected.Take(3).ToArray(), matches[active.Id].Select(c => c.ChunkId).ToArray());
        Assert.IsTrue(matches[active.Id].All(c => c.Content.Length == 160 && c.IsTruncated));
        Assert.AreEqual("overview", (await _database.Store.GetPrimaryChunkAsync(_scope, active.Id))!.Content);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _database.Store.GetMatchedChunksAsync(_scope, UnitVector(0), [active.Id], 9, 160));
    }

    [TestMethod]
    public async Task MatchedChunkEvidenceLoadsNonPrimaryAndPreservesScopeAndBounds()
    {
        var active = await InsertMemoryAsync(_scope, "matched", MemoryType.Fact, "overview", UnitVector(1));
        var chunk = await _database.Store.InsertChunkAsync(_scope, new MemoryChunkEntry
        { EntityId = active.Id, ChunkIndex = 1, Content = new string('x', 1000), Embedding = UnitVector(0) });
        var foreign = await InsertMemoryAsync(_database.CreateScopeKey("matched-other"), "other", MemoryType.Fact, "foreign", UnitVector(0));
        var cold = await InsertMemoryAsync(_scope, "matched-cold", MemoryType.Fact, "cold", UnitVector(0));
        cold.Temperature = MemoryTemperature.Cold;
        await _database.Store.UpdateEntityAsync(_scope, cold);
        var matches = await _database.Store.GetMatchedChunkEvidenceAsync(_scope, UnitVector(0), [active.Id, foreign.Id, cold.Id], 160);
        Assert.HasCount(1, matches);
        Assert.AreEqual(chunk.ChunkId, matches[active.Id].ChunkId);
        Assert.AreEqual(1, matches[active.Id].ChunkIndex);
        Assert.AreEqual(new string('x', 160), matches[active.Id].Content);
        Assert.IsTrue(matches[active.Id].IsTruncated);
        Assert.AreEqual("overview", (await _database.Store.GetPrimaryChunkAsync(_scope, active.Id))!.Content);
    }

    [TestMethod]
    public async Task CandidatePreviewsAreBoundedAndDoNotExposeColdOrOtherScopes()
    {
        var active = await InsertMemoryAsync(_scope, "active", MemoryType.Fact, new string('x', 1000) + "hidden-tail", UnitVector(0));
        var cold = await InsertMemoryAsync(_scope, "cold preview", MemoryType.Fact, "cold secret", UnitVector(0));
        cold.Temperature = MemoryTemperature.Cold;
        await _database.Store.UpdateEntityAsync(_scope, cold);
        var foreign = await InsertMemoryAsync(_database.CreateScopeKey("preview-other"), "foreign", MemoryType.Fact, "other secret", UnitVector(0));
        var previews = await _database.Store.GetCandidatePreviewsAsync(_scope, [active.Id, cold.Id, foreign.Id], 160);
        Assert.HasCount(1, previews);
        Assert.AreEqual(new string('x', 160), previews[active.Id]);
        Assert.AreEqual("active", (await _database.Store.GetEntityByIdAsync(_scope, active.Id))!.Description);
    }

    [TestMethod]
    public async Task HybridCandidatesKeepFiveCloseFactsAndOtherTypesWithinEightSlots()
    {
        var facts = new List<Guid>();
        for (var i = 0; i < 5; i++)
            facts.Add((await InsertMemoryAsync(_scope, $"fact {i}", MemoryType.Fact, "fact", UnitVector(0))).Id);
        var procedure = await InsertMemoryAsync(_scope, "procedure", MemoryType.Procedural, "steps", UnitVector(1));
        var rule = await InsertMemoryAsync(_scope, "rule", MemoryType.Rule, "constraint", UnitVector(1));
        var observation = await InsertMemoryAsync(_scope, "observation", MemoryType.Observation, "observation", UnitVector(1));
        for (var i = 0; i < 6; i++)
            await InsertMemoryAsync(_scope, $"distractor {i}", MemoryType.Fact, "distractor", UnitVector(2));
        var hybrid = await _database.Store.FindHybridCandidateHeadersAsync(_scope, UnitVector(0), 8);
        CollectionAssert.AreEquivalent(facts.Concat(new[] { procedure.Id, rule.Id, observation.Id }).ToArray(),
            hybrid.Select(h => h.MemoryId).ToArray());
        procedure.Temperature = MemoryTemperature.Cold;
        await _database.Store.UpdateEntityAsync(_scope, procedure);
        var excluded = await _database.Store.FindHybridCandidateHeadersAsync(_scope, UnitVector(0), 8, [facts[0]]);
        Assert.HasCount(8, excluded);
        Assert.IsFalse(excluded.Any(h => h.MemoryId == procedure.Id || h.MemoryId == facts[0]));
    }

    [TestMethod]
    public async Task DiverseCandidatesReserveSpaceForOtherTypesWithinSameLimit()
    {
        for (var i = 0; i < 5; i++)
            await InsertMemoryAsync(_scope, $"region {i}", MemoryType.Fact, "region", UnitVector(0));
        var procedure = await InsertMemoryAsync(_scope, "procedure", MemoryType.Procedural, "steps", UnitVector(1));
        var ordinary = await _database.Store.FindCandidateHeadersAsync(_scope, UnitVector(0), 2);
        Assert.IsFalse(ordinary.Any(h => h.MemoryId == procedure.Id));
        var diverse = await _database.Store.FindDiverseCandidateHeadersAsync(_scope, UnitVector(0), 2);
        Assert.HasCount(2, diverse);
        Assert.IsTrue(diverse.Any(h => h.MemoryId == procedure.Id));
        var excluded = await _database.Store.FindDiverseCandidateHeadersAsync(_scope, UnitVector(0), 2, [procedure.Id]);
        Assert.HasCount(2, excluded);
        Assert.IsFalse(excluded.Any(h => h.MemoryId == procedure.Id));
        procedure.Temperature = MemoryTemperature.Cold;
        await _database.Store.UpdateEntityAsync(_scope, procedure);
        Assert.IsFalse((await _database.Store.FindDiverseCandidateHeadersAsync(_scope, UnitVector(0), 2))
            .Any(h => h.MemoryId == procedure.Id));
    }

    [TestMethod]
    public async Task SemanticCandidatesExcludeColdOtherScopesAndExcludedBeforeDistinctLimit()
    {
        var first = await InsertMemoryAsync(_scope, "first", MemoryType.Fact, "first", UnitVector(0));
        await _database.Store.InsertChunkAsync(_scope, new MemoryChunkEntry
        { EntityId = first.Id, Content = "second chunk", ChunkIndex = 1, Embedding = UnitVector(0) });
        var second = await InsertMemoryAsync(_scope, "second", MemoryType.Rule, "second", UnitVector(0));
        var cold = await InsertMemoryAsync(_scope, "cold", MemoryType.Fact, "cold", UnitVector(0));
        cold.Temperature = MemoryTemperature.Cold;
        await _database.Store.UpdateEntityAsync(_scope, cold);
        await InsertMemoryAsync(_database.CreateScopeKey("other"), "foreign", MemoryType.Fact, "foreign", UnitVector(0));
        var candidates = await _database.Store.FindCandidateHeadersAsync(_scope, UnitVector(0), 2);
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, candidates.Select(h => h.MemoryId).ToArray());
        var excluded = await _database.Store.FindCandidateHeadersAsync(_scope, UnitVector(0), 1, [second.Id]);
        Assert.AreEqual(first.Id, excluded.Single().MemoryId);
    }

    [TestMethod]
    public async Task CancelledMutationRollsBackBeforeReleasingScopeLock()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _database.Store.MutateAsync(_scope, async ct =>
        {
            await InsertMemoryAsync(_scope, "cancelled", MemoryType.Fact, "must disappear", UnitVector(0));
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return true;
        }, cancellation.Token));
        Assert.IsEmpty(await _database.Store.GetHeadersAsync(_scope, 100));
        await _database.Store.MutateAsync(_scope, _ => Task.FromResult(true), default);
    }

    [TestMethod]
    public async Task MutationFailureRollsBackEntityChunkAndIndex()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.Store.MutateAsync<int>(_scope, async ct =>
        {
            var entry = await InsertMemoryAsync(_scope, "rollback", MemoryType.Fact, "must disappear", UnitVector(0));
            await _database.Store.ModifyIndexContentAsync(_scope, _ => "{\"test\":true}", ct);
            throw new InvalidOperationException("injected failure after all writes");
        }, default));
        Assert.IsEmpty(await _database.Store.GetHeadersAsync(_scope, 100));
        Assert.IsNull(await _database.Store.GetIndexContentAsync(_scope));
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM mem.MemoryChunk WHERE ScopeKey=@scope", connection);
        command.Parameters.AddWithValue("@scope", _scope);
        Assert.AreEqual(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task IndependentStoreInstancesSerializeSharedScopeCorrections()
    {
        var entry = await InsertMemoryAsync(_scope, "counter", MemoryType.Fact, "0", UnitVector(0));
        var stores = Enumerable.Range(0, 6).Select(_ => new SqlMemoryStore(_database.Options, _database.Configuration, NullLoggerFactory.Instance)).ToArray();
        await Task.WhenAll(stores.Select(store => store.MutateAsync(_scope, async ct =>
        {
            var chunk = (await store.GetPrimaryChunkAsync(_scope, entry.Id, ct))!;
            await Task.Delay(15, ct);
            chunk.Content = (int.Parse(chunk.Content) + 1).ToString();
            await store.UpdateChunkAsync(_scope, chunk, ct);
            return true;
        }, default)));
        Assert.AreEqual("6", (await _database.Store.GetPrimaryChunkAsync(_scope, entry.Id))!.Content);
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

    [TestMethod]
    public async Task ColdMemory_IsExcludedFromHeadersButRetainedInArchiveAndCanBeRestored()
    {
        var entry = await InsertMemoryAsync(_scope, "Archived policy", MemoryType.Rule, "Prior policy", UnitVector(0));
        entry.Temperature = MemoryTemperature.Cold;
        await _database.Store.UpdateEntityAsync(_scope, entry);
        Assert.IsFalse((await _database.Store.GetHeadersAsync(_scope, 20)).Any(h => h.MemoryId == entry.Id));
        Assert.IsTrue((await _database.Store.VectorSearchAsync(_scope, UnitVector(0), 10)).Any(r => r.Entry.Id == entry.Id));
        entry.Temperature = MemoryTemperature.Warm;
        await _database.Store.UpdateEntityAsync(_scope, entry);
        Assert.IsTrue((await _database.Store.GetHeadersAsync(_scope, 20)).Any(h => h.MemoryId == entry.Id));
        var chunk = (await _database.Store.GetPrimaryChunkAsync(_scope, entry.Id))!;
        chunk.Content = "Changed content without a vector";
        chunk.Embedding = null;
        await _database.Store.UpdateChunkAsync(_scope, chunk);
        Assert.HasCount(0, await _database.Store.VectorSearchAsync(_scope, UnitVector(0), 10),
            "Changed content must not keep the old content's vector.");
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
