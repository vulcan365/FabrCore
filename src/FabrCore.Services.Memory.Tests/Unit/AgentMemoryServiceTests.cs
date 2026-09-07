using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Services.Memory.Tests.Infrastructure;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class AgentMemoryServiceTests
{
    [TestMethod]
    public async Task SaveMemory_NewMemoryPersistsChunkIndexScopeAndAudit()
    {
        var fixture = new ServiceFixture();
        fixture.ConfigureNewEntityPersistence();

        var result = await fixture.Service.SaveMemoryAsync(
            "API casing",
            MemoryType.Rule,
            "All API responses use camelCase.",
            "Response casing convention",
            new Dictionary<string, string> { ["source"] = "user" });

        Assert.AreNotEqual(Guid.Empty, result.Id);
        Assert.AreEqual(fixture.Scope, result.ScopeKey);
        Assert.AreEqual(MemoryTemperature.Warm, result.Temperature);
        Assert.AreEqual("All API responses use camelCase.", result.Content);
        Assert.AreEqual("user", result.Metadata!["source"]);
        await fixture.Store.Received(1).InsertChunkAsync(
            fixture.Scope,
            Arg.Is<MemoryChunkEntry>(c => c != null && c.EntityId == result.Id && c.ChunkIndex == 0),
            Arg.Any<CancellationToken>());
        await fixture.Index.Received(1).AddIndexEntryAsync(
            fixture.Scope,
            Arg.Is<MemoryIndexEntry>(e => e != null && e.MemoryId == result.Id && e.Type == MemoryType.Rule),
            Arg.Any<CancellationToken>());
        await fixture.ScopeService.Received(1).EnsureScopeAsync(
            fixture.Scope, false, Arg.Any<CancellationToken>());
        await fixture.Audit.Received(1).RecordAsync(
            "MemorySaved", fixture.Scope, result.Id,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SaveMemory_EmbeddingFailureStillStoresMemoryWithoutVector()
    {
        var fixture = new ServiceFixture();
        fixture.Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<float[]>(_ => throw new InvalidOperationException("embedding service unavailable"));
        fixture.ConfigureNewEntityPersistence(configureEmbedding: false);

        var result = await fixture.Service.SaveMemoryAsync(
            "Stable fact", MemoryType.Fact, "The staging database is shared with QA.");

        Assert.IsNull(result.Embedding);
        await fixture.Store.Received(1).InsertChunkAsync(
            fixture.Scope,
            Arg.Is<MemoryChunkEntry>(c => c != null && c.Embedding == null),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SaveMemory_DisallowedTaxonomyFailsBeforeStorage()
    {
        var options = new AgentMemoryOptions
        {
            AllowedMemoryTypes = [MemoryType.Fact]
        };
        var fixture = new ServiceFixture(options: options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveMemoryAsync("rule", MemoryType.Rule, "Never do this."));

        StringAssert.Contains(exception.Message, "taxonomy validation failed");
        await fixture.Store.DidNotReceive().GenerateEmbeddingAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SaveMemory_SimilarSameTypeMergesInsteadOfDuplicating()
    {
        var fixture = new ServiceFixture();
        var existing = new MemoryEntry
        {
            Id = Guid.NewGuid(),
            ScopeKey = fixture.Scope,
            Title = "Deployment window",
            Type = MemoryType.Rule,
            Description = "Old description"
        };
        var chunk = new MemoryChunkEntry
        {
            ChunkId = Guid.NewGuid(),
            EntityId = existing.Id,
            Content = "Deploy on Tuesdays."
        };
        fixture.Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 1, 0, 0 });
        fixture.Store.FindSimilarByContentAsync(
                fixture.Scope, Arg.Any<float[]>(), 3, Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new[] { (existing, chunk, 0.01d) });
        fixture.Store.UpdateChunkAsync(fixture.Scope, chunk, Arg.Any<CancellationToken>())
            .Returns(chunk);
        fixture.Store.UpdateEntityAsync(fixture.Scope, existing, Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await fixture.Service.SaveMemoryAsync(
            "Deployment policy", MemoryType.Rule, "Deploy on Thursdays.", "Updated window");

        Assert.AreEqual(existing.Id, result.Id);
        StringAssert.Contains(result.Content!, "Deploy on Tuesdays.");
        StringAssert.Contains(result.Content!, "Deploy on Thursdays.");
        Assert.AreEqual("Updated window", result.Description);
        await fixture.Store.DidNotReceive().InsertEntityAsync(
            Arg.Any<string>(), Arg.Any<MemoryEntry>(), Arg.Any<CancellationToken>());
        await fixture.Audit.Received(1).RecordAsync(
            "MemoryMerged", fixture.Scope, existing.Id,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UpdateMemory_PartialUpdateRegeneratesEmbeddingAndIndex()
    {
        var fixture = new ServiceFixture();
        var id = Guid.NewGuid();
        var existing = new MemoryEntry
        {
            Id = id,
            ScopeKey = fixture.Scope,
            Title = "Old title",
            Type = MemoryType.Observation,
            Description = "old description",
            Temperature = MemoryTemperature.Warm
        };
        var chunk = new MemoryChunkEntry
        {
            ChunkId = Guid.NewGuid(), EntityId = id, Content = "old content"
        };
        fixture.Store.GetEntityByIdAsync(fixture.Scope, id, Arg.Any<CancellationToken>())
            .Returns(existing);
        fixture.Store.GetPrimaryChunkAsync(fixture.Scope, id, Arg.Any<CancellationToken>())
            .Returns(chunk);
        fixture.Store.UpdateEntityAsync(fixture.Scope, existing, Arg.Any<CancellationToken>())
            .Returns(existing);
        fixture.Store.UpdateChunkAsync(fixture.Scope, chunk, Arg.Any<CancellationToken>())
            .Returns(chunk);
        fixture.Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0, 1, 0 });

        var result = await fixture.Service.UpdateMemoryAsync(
            id,
            title: "Durable rule",
            type: MemoryType.Rule,
            content: "Use the v2 endpoint.",
            temperature: MemoryTemperature.Cold);

        Assert.AreEqual("Durable rule", result.Title);
        Assert.AreEqual(MemoryType.Rule, result.Type);
        Assert.AreEqual(MemoryTemperature.Cold, result.Temperature);
        Assert.AreEqual("Use the v2 endpoint.", result.Content);
        CollectionAssert.AreEqual(new float[] { 0, 1, 0 }, chunk.Embedding!);
        await fixture.Index.Received(1).RemoveIndexEntryAsync(fixture.Scope, id, Arg.Any<CancellationToken>());
        await fixture.Index.DidNotReceive().AddIndexEntryAsync(
            fixture.Scope, Arg.Any<MemoryIndexEntry>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ForgetMemory_RemovesHotIndexAndEntity()
    {
        var fixture = new ServiceFixture();
        var id = Guid.NewGuid();
        fixture.Store.DeleteEntityAsync(fixture.Scope, id, Arg.Any<CancellationToken>())
            .Returns(true);

        var deleted = await fixture.Service.ForgetMemoryAsync(id);

        Assert.IsTrue(deleted);
        await fixture.Index.Received(1).RemoveIndexEntryAsync(
            fixture.Scope, id, Arg.Any<CancellationToken>());
        await fixture.Audit.Received(1).RecordAsync(
            "MemoryForgotten", fixture.Scope, id,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void FormatRecallContext_UsesMarkersAndIncludesGraphAndFreshnessDetails()
    {
        var fixture = new ServiceFixture();
        var result = new MemoryRecallResult
        {
            HotIndex = new MemoryIndex
            {
                Entries =
                [
                    new MemoryIndexEntry
                    {
                        Title = "Preferred format", Type = MemoryType.Instruction,
                        DescriptionHook = "Use concise tables", IsPointInTime = false
                    }
                ]
            },
            WarmMemories =
            [
                new MemoryEntry
                {
                    Title = "Inventory count", Type = MemoryType.Observation,
                    Description = "Yesterday's count", Content = "There were 12 items.",
                    IsPointInTime = true,
                    Relationships =
                    [
                        new MemoryRelationshipEntry
                        {
                            RelationshipType = "belongs_to",
                            RelatedEntityType = MemoryType.Fact,
                            RelatedEntityTitle = "Warehouse A"
                        }
                    ]
                }
            ],
            FreshnessWarnings = ["Inventory count: verify current values"]
        };

        var context = fixture.Service.FormatRecallContext(result);

        StringAssert.StartsWith(context, AgentMemoryService.MemoryContextStart);
        StringAssert.Contains(context, "[Instruction] Preferred format");
        StringAssert.Contains(context, "[Observation] [snapshot] Inventory count");
        StringAssert.Contains(context, "belongs_to → [Fact] Warehouse A");
        StringAssert.Contains(context, "verify current values");
        StringAssert.EndsWith(context, AgentMemoryService.MemoryContextEnd);
    }

    [TestMethod]
    public async Task Recall_LoadsSelectedContentAndDeduplicatesGraphExpansion()
    {
        var fixture = new ServiceFixture();
        var selectedId = Guid.NewGuid();
        var relatedId = Guid.NewGuid();
        var selected = new MemoryEntry
        {
            Id = selectedId, ScopeKey = fixture.Scope, Title = "Selected", Type = MemoryType.Fact
        };
        var related = new MemoryEntry
        {
            Id = relatedId, ScopeKey = fixture.Scope, Title = "Related", Type = MemoryType.Rule
        };
        var plan = new RetrievalPlan
        {
            Steps = [RetrievalStep.HeaderScanLlmSelect, RetrievalStep.GraphExpand]
        };
        var header = new MemoryHeader
        {
            MemoryId = selectedId,
            Title = "Selected",
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };
        fixture.Planner.CreatePlanAsync(Arg.Any<string>(), Arg.Any<MemoryIndex>(), Arg.Any<CancellationToken>())
            .Returns(plan);
        fixture.Index.GetIndexAsync(fixture.Scope, Arg.Any<CancellationToken>())
            .Returns(new MemoryIndex());
        fixture.Retriever.ScanMemoryHeadersAsync(
                fixture.Scope, Arg.Any<int>(), Arg.Any<MemoryType?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { header });
        fixture.Retriever.SelectRelevantMemoriesAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<MemoryHeader>>(), Arg.Any<int>(),
                Arg.Any<IReadOnlySet<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { selectedId });
        fixture.Store.GetEntityByIdAsync(fixture.Scope, selectedId, Arg.Any<CancellationToken>())
            .Returns(selected);
        fixture.Store.GetPrimaryChunkAsync(fixture.Scope, selectedId, Arg.Any<CancellationToken>())
            .Returns(new MemoryChunkEntry { EntityId = selectedId, Content = "selected content" });
        fixture.Store.GetRelationshipsAsync(fixture.Scope, selectedId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemoryRelationshipEntry>());
        fixture.Retriever.GetRelatedEntitiesAsync(
                fixture.Scope, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { selected, related });
        fixture.Retriever.GetFreshnessWarning(header).Returns("stale warning");

        var result = await fixture.Service.RecallAsync("deployment question");

        CollectionAssert.AreEquivalent(
            new[] { selectedId, relatedId }, result.WarmMemories.Select(m => m.Id).ToArray());
        Assert.AreEqual("selected content", result.WarmMemories.Single(m => m.Id == selectedId).Content);
        CollectionAssert.Contains(result.FreshnessWarnings, "Selected: stale warning");
    }

    [TestMethod]
    public async Task ExtractMemories_ParsesDurableItemsStripsRecallContextAndCreatesRelationships()
    {
        var client = FakeChatClient.WithText("""
            {"memories":[
              {"title":"API convention","type":"Rule","content":"Use camelCase JSON.","description":"JSON casing rule","is_point_in_time":false,"related_to":["Deployment workflow"]},
              {"title":"Deployment workflow","type":"Procedural","content":"Validate, deploy, then smoke test.","description":"Release steps","is_point_in_time":false}
            ]}
            """);
        var services = new ServiceCollection()
            .AddSingleton<IFabrCoreChatClientService>(new TestChatClientService(client))
            .BuildServiceProvider();
        var fixture = new ServiceFixture(services: services);
        fixture.ConfigureNewEntityPersistence();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Remember our release practices."),
            new(ChatRole.Assistant,
                $"Previously recalled {AgentMemoryService.MemoryContextStart}do not re-extract this secret{AgentMemoryService.MemoryContextEnd}")
        };

        var extracted = await fixture.Service.ExtractMemoriesAsync(messages);

        Assert.HasCount(2, extracted);
        CollectionAssert.AreEquivalent(
            new[] { MemoryType.Rule, MemoryType.Procedural }, extracted.Select(m => m.Type).ToArray());
        var prompt = string.Join("\n", client.ReceivedMessages.Single().Select(m => m.Text));
        Assert.DoesNotContain("do not re-extract this secret", prompt);
        await fixture.Store.Received(1).InsertRelationshipAsync(
            fixture.Scope,
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            "related_to",
            Arg.Any<string?>(),
            Arg.Any<double>(),
            Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UpdateMemory_RestoresColdMemoryToActiveIndex()
    {
        var f = new ServiceFixture();
        var entry = new MemoryEntry { Id = Guid.NewGuid(), Title = "Policy", Type = MemoryType.Rule, Temperature = MemoryTemperature.Cold };
        f.Store.GetEntityByIdAsync(f.Scope, entry.Id, Arg.Any<CancellationToken>()).Returns(entry);
        f.Store.UpdateEntityAsync(f.Scope, entry, Arg.Any<CancellationToken>()).Returns(entry);
        var result = await f.Service.UpdateMemoryAsync(entry.Id, temperature: MemoryTemperature.Warm);
        Assert.AreEqual(MemoryTemperature.Warm, result.Temperature);
        await f.Index.Received(1).AddIndexEntryAsync(f.Scope,
            Arg.Is<MemoryIndexEntry>(e => e.MemoryId == entry.Id), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void FormatRecallContext_IncludesArchiveOnlyAndSummaryOnlyResults()
    {
        var f = new ServiceFixture();
        var archive = new MemoryRecallResult { ArchiveResults = [new MemorySearchResult {
            Entry = new MemoryEntry { Title = "Old policy", Content = "Historical content" }, FreshnessWarning = "Verify source" }] };
        var formatted = f.Service.FormatRecallContext(archive);
        StringAssert.Contains(formatted, "Historical content");
        StringAssert.Contains(formatted, "Verify source");
        StringAssert.StartsWith(formatted, AgentMemoryService.MemoryContextStart);
        StringAssert.EndsWith(formatted, AgentMemoryService.MemoryContextEnd);
        var summary = new MemoryRecallResult { SummaryNodes = [new MemorySummaryNode { Topic = "Policy", Summary = "Topic rollup" }] };
        StringAssert.Contains(f.Service.FormatRecallContext(summary), "Topic rollup");
    }

    [TestMethod]
    public async Task SaveMemory_CancellationDoesNotFallBackToPersistence()
    {
        var f = new ServiceFixture();
        f.Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<float[]>(_ => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.SaveMemoryAsync("Fact", MemoryType.Fact, "Durable knowledge"));
        await f.Store.DidNotReceive().InsertEntityAsync(Arg.Any<string>(), Arg.Any<MemoryEntry>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UpdateMemory_RepairsMissingChunkAndClearsStaleVectorOnEmbeddingFailure()
    {
        var f = new ServiceFixture();
        var entry = new MemoryEntry { Id = Guid.NewGuid(), Title = "Fact", Type = MemoryType.Fact };
        f.Store.GetEntityByIdAsync(f.Scope, entry.Id, Arg.Any<CancellationToken>()).Returns(entry);
        f.Store.UpdateEntityAsync(f.Scope, entry, Arg.Any<CancellationToken>()).Returns(entry);
        f.Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<float[]>(_ => throw new InvalidOperationException("offline"));
        await f.Service.UpdateMemoryAsync(entry.Id, content: "New content");
        await f.Store.Received(1).InsertChunkAsync(f.Scope,
            Arg.Is<MemoryChunkEntry>(c => c.EntityId == entry.Id && c.Content == "New content" && c.Embedding == null), Arg.Any<CancellationToken>());
        var oldChunk = new MemoryChunkEntry { EntityId = entry.Id, Content = "Old content", Embedding = [1, 0, 0] };
        f.Store.GetPrimaryChunkAsync(f.Scope, entry.Id, Arg.Any<CancellationToken>()).Returns(oldChunk);
        await f.Service.UpdateMemoryAsync(entry.Id, content: "Changed content");
        await f.Store.Received(1).UpdateChunkAsync(f.Scope,
            Arg.Is<MemoryChunkEntry>(c => c.Content == "Changed content" && c.Embedding == null), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SaveMemory_MergeWriteFailureDoesNotCreateDuplicate()
    {
        var f = new ServiceFixture();
        var entry = new MemoryEntry { Id = Guid.NewGuid(), Title = "Fact", Type = MemoryType.Fact };
        var chunk = new MemoryChunkEntry { EntityId = entry.Id, Content = "Old fact" };
        f.Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0, 0 });
        f.Store.FindSimilarByContentAsync(f.Scope, Arg.Any<float[]>(), 3, Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new[] { (entry, chunk, 0.01d) });
        f.Store.UpdateChunkAsync(f.Scope, chunk, Arg.Any<CancellationToken>())
            .Returns<MemoryChunkEntry>(_ => throw new InvalidOperationException("write failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SaveMemoryAsync("Fact", MemoryType.Fact, "Updated fact"));
        await f.Store.DidNotReceive().InsertEntityAsync(Arg.Any<string>(), Arg.Any<MemoryEntry>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Recall_VectorPathDoesNotSurfaceColdContentAsWarm()
    {
        var f = new ServiceFixture();
        f.Planner.CreatePlanAsync(Arg.Any<string>(), Arg.Any<MemoryIndex>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalPlan { Steps = [RetrievalStep.VectorOnly] });
        f.Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0, 0 });
        var archived = new MemoryEntry { Id = Guid.NewGuid(), Temperature = MemoryTemperature.Cold };
        var active = new MemoryEntry { Id = Guid.NewGuid(), Temperature = MemoryTemperature.Warm };
        f.Store.VectorSearchAsync(f.Scope, Arg.Any<float[]>(), Arg.Any<int>(), null, Arg.Any<CancellationToken>())
            .Returns(new[] { new MemorySearchResult { Entry = archived }, new MemorySearchResult { Entry = active } });
        var result = await f.Service.RecallAsync("policy");
        Assert.HasCount(1, result.WarmMemories);
        Assert.AreEqual(active.Id, result.WarmMemories[0].Id);
    }

    private sealed class ServiceFixture
    {
        public ServiceFixture(
            AgentMemoryOptions? options = null,
            IServiceProvider? services = null)
        {
            Options = options ?? new AgentMemoryOptions();
            Store = Substitute.For<IMemoryStore>();
            Index = Substitute.For<IMemoryIndexManager>();
            Retriever = Substitute.For<IMemoryRetriever>();
            Compactor = Substitute.For<IMemoryCompactor>();
            Planner = Substitute.For<IRetrievalPlanner>();
            SummaryTree = Substitute.For<IMemorySummaryTree>();
            ScopeService = Substitute.For<IMemoryScopeService>();
            Audit = Substitute.For<IMemoryAuditLog>();
            Index.GetIndexAsync(Scope, Arg.Any<CancellationToken>())
                .Returns(new MemoryIndex());
            Service = new AgentMemoryService(
                Scope, Store, Index, Retriever, Compactor, Planner, SummaryTree,
                ScopeService, Audit, Options,
                services ?? new ServiceCollection().BuildServiceProvider(),
                NullLoggerFactory.Instance);
        }

        public string Scope { get; } = "unit:service";
        public AgentMemoryOptions Options { get; }
        public IMemoryStore Store { get; }
        public IMemoryIndexManager Index { get; }
        public IMemoryRetriever Retriever { get; }
        public IMemoryCompactor Compactor { get; }
        public IRetrievalPlanner Planner { get; }
        public IMemorySummaryTree SummaryTree { get; }
        public IMemoryScopeService ScopeService { get; }
        public IMemoryAuditLog Audit { get; }
        public AgentMemoryService Service { get; }

        public void ConfigureNewEntityPersistence(bool configureEmbedding = true)
        {
            if (configureEmbedding)
            {
                Store.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new float[] { 1, 0, 0 });
            }

            Store.FindSimilarByContentAsync(
                    Scope, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<(MemoryEntry Entity, MemoryChunkEntry Chunk, double Distance)>());
            Store.InsertEntityAsync(Scope, Arg.Any<MemoryEntry>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var entry = call.ArgAt<MemoryEntry>(1);
                    entry.Id = Guid.NewGuid();
                    entry.ScopeKey = Scope;
                    entry.CreatedAt = entry.UpdatedAt = DateTime.UtcNow;
                    return entry;
                });
            Store.InsertChunkAsync(Scope, Arg.Any<MemoryChunkEntry>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var chunk = call.ArgAt<MemoryChunkEntry>(1);
                    chunk.ChunkId = Guid.NewGuid();
                    return chunk;
                });
        }
    }
}
