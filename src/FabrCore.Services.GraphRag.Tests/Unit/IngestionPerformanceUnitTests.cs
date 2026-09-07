using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class IngestionPerformanceUnitTests
{
    private static readonly IngestSourceDocument Source = new(
        "performance.md",
        "Markdown",
        "performance.md",
        "Performance",
        null,
        null,
        "",
        null);

    [TestMethod]
    public async Task PolicyRelations_ApplyEnumAndGuidanceOnlyToGraphAndCombinedCalls()
    {
        var captured = new System.Collections.Concurrent.ConcurrentBag<(string Prompt, ChatOptions? Options)>();
        var client = new ScriptedChatClient((_, _, _) => Task.FromResult(JsonResponse(1)));
        client.Observe = (prompt, options) => captured.Add((prompt, options));
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionJsonSchema"] = "true",
                ["GraphRag:Ingestion:UsePolicyRelations"] = "true",
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
                ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "1"
            });
        await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = new string('x', 700) }, useDocumentPlan: true);
        await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "Agency uses a resource." }, useDocumentPlan: true);
        Assert.HasCount(5, captured);
        foreach (var (prompt, options) in captured)
        {
            var props = ((ChatResponseFormatJson)options!.ResponseFormat!).Schema!.Value.GetProperty("properties");
            if (!props.TryGetProperty("relationships", out var relationships))
            {
                Assert.DoesNotContain("Policy relationship contract", prompt);
                continue;
            }
            StringAssert.Contains(prompt, "Policy relationship contract");
            var types = relationships.GetProperty("items").GetProperty("properties").GetProperty("type").GetProperty("enum");
            CollectionAssert.AreEquivalent(PolicyRelationGuidance.Types, types.EnumerateArray().Select(t => t.GetString()).ToArray());
            StringAssert.Contains(prompt, "recommendation into a requirement");
        }
    }

    [TestMethod]
    public async Task StructuredTaxonomy_SeparatesMetadataAndRejectsInventedReuse()
    {
        var prompt = KnowledgeIngestionService.BuildExtractionPromptForTesting(["Policy"], Source,
            [("Engineering", "technical")], [("Open Data", "Engineering", "governance")], structuredTaxonomyNames: true);
        StringAssert.Contains(prompt, "\"name\":\"Open Data\"");
        StringAssert.Contains(prompt, "\"domainName\":\"Engineering\"");
        Assert.DoesNotContain("Open Data (in Engineering)", prompt);
        foreach (var (name, isNew, expectedDomain) in new[]
        {
            ("Open Data", false, "Engineering"),
            ("Open Data (in Engineering)", false, (string?)null),
            ("New Subject", true, "Engineering")
        })
        {
            var json = $$"""
                {"domain":{"name":"Engineering","description":"technical","isNew":false,"confidence":0.95},
                 "category":{"name":"{{name}}","description":"governance","isNew":{{isNew.ToString().ToLowerInvariant()}},"confidence":0.95},
                 "entities":[{"name":"Agency","entityType":"Organization","description":"Agency"}],"relationships":[]}
                """;
            var client = new ScriptedChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));
            var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
                embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
                { ["GraphRag:Ingestion:UseStructuredTaxonomyNames"] = "true" });
            var result = await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "Policy" },
                useDocumentPlan: true, domains: [("Engineering", "technical")], categories: [("Open Data", "Engineering", "governance")]);
            Assert.AreEqual(expectedDomain, result.DomainName);
            CollectionAssert.Contains(result.EntityNames.ToArray(), "Agency");
            Assert.AreEqual(1, result.ChatCallCount);
        }
    }

    [TestMethod]
    public async Task SchemaExtraction_RoutesGraphTaxonomyAndCombinedWithoutChangingPrompts()
    {
        var captured = new System.Collections.Concurrent.ConcurrentBag<(string Prompt, ChatOptions? Options)>();
        var client = new ScriptedChatClient((prompt, _, _) => Task.FromResult(JsonResponse(1)));
        client.Observe = (prompt, options) => captured.Add((prompt, options));
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionJsonSchema"] = "true",
                ["GraphRag:Ingestion:ExtractionMaxOutputTokens"] = "2000",
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
                ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "1"
            });
        await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = new string('x', 700) }, useDocumentPlan: true);
        await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "A uses B." }, useDocumentPlan: true);
        Assert.HasCount(5, captured);
        foreach (var (prompt, options) in captured)
        {
            Assert.AreEqual(2000, options!.MaxOutputTokens);
            var format = (ChatResponseFormatJson)options.ResponseFormat!;
            var props = format.Schema!.Value.GetProperty("properties");
            var taxonomyOnly = prompt.Contains("document classification only");
            var graphOnly = prompt.Contains("Do not classify domains or categories");
            Assert.AreEqual(!graphOnly, props.TryGetProperty("domain", out _));
            Assert.AreEqual(!taxonomyOnly, props.TryGetProperty("entities", out _));
            Assert.DoesNotContain("at most 120", prompt);
        }
    }

    [TestMethod]
    public async Task RelationGuidance_AppliesToGraphAndCombinedButNotClassifier()
    {
        var captured = new System.Collections.Concurrent.ConcurrentBag<string>();
        var client = new ScriptedChatClient((prompt, _, _) =>
        {
            captured.Add(prompt);
            return Task.FromResult(JsonResponse(1));
        });
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionRelationGuidance"] = "true",
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
                ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "1"
            });
        await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = new string('x', 700) }, useDocumentPlan: true);
        await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "A uses B." }, useDocumentPlan: true);
        Assert.HasCount(5, captured);
        Assert.AreEqual(1, captured.Count(p => p.Contains("document classification only")));
        foreach (var prompt in captured)
            Assert.AreEqual(!prompt.Contains("document classification only"), prompt.StartsWith(ExtractionRelationGuidance.Text));
        Assert.AreEqual(4, captured.Count(p => p.Contains("Publication") && p.Contains("PUBLISHED_BY")));
    }

    [TestMethod]
    public async Task EvidenceExtraction_FailsClosedWithoutRepairAndAcceptsSupportedQuote()
    {
        var includeEvidence = false;
        var client = new ScriptedChatClient((_, _, _) =>
        {
            var json = JsonResponse(1).Text;
            if (includeEvidence) json = json.Replace("\"confidence\":0.9}]", "\"confidence\":0.9,\"evidence\":\"A uses B.\"}]");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        });
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionJsonSchema"] = "true",
                ["GraphRag:Ingestion:UseExtractionEvidence"] = "true"
            });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "A uses B." }, useDocumentPlan: true));
        includeEvidence = true;
        var result = await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "A uses B." }, useDocumentPlan: true);
        Assert.AreEqual(1, result.ChatCallCount);
        Assert.AreEqual(1, result.RelationshipCount);
    }

    [TestMethod]
    public async Task SourceSpans_RepairOnceAfterMergingAndRejectUnrequestedEntities()
    {
        var repairs = 0;
        var invalidRepair = false;
        var unresolvedRepair = false;
        var client = new ScriptedChatClient((prompt, _, _) =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(JsonResponse(1).Text)!;
            if (prompt.StartsWith("ENDPOINT REPAIR:"))
            {
                Interlocked.Increment(ref repairs);
                var repaired = root["entities"]![1]!.DeepClone();
                if (invalidRepair) repaired["name"] = "Unrequested";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    new System.Text.Json.Nodes.JsonObject { ["entities"] = new System.Text.Json.Nodes.JsonArray(repaired),
                        ["unresolvedNames"] = unresolvedRepair ? new System.Text.Json.Nodes.JsonArray("Entity-1-B") : new System.Text.Json.Nodes.JsonArray() }.ToJsonString())));
            }
            if (prompt.Contains("document classification only")) return Task.FromResult(JsonResponse(1));
            var id = System.Text.RegularExpressions.Regex.Match(prompt, @"\[SOURCE-SPAN (s_[0-9a-f]+)\]").Groups[1].Value;
            Assert.IsFalse(string.IsNullOrWhiteSpace(id));
            root["entities"]!.AsArray().RemoveAt(1);
            root["relationships"]![0]!["sourceIds"] = new System.Text.Json.Nodes.JsonArray(id);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, root.ToJsonString())));
        });
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionJsonSchema"] = "true",
                ["GraphRag:Ingestion:UseExtractionSourceSpans"] = "true",
                ["GraphRag:Ingestion:RepairExtractionEndpoints"] = "true",
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
                ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "1"
            });
        var result = await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = new string('x', 700) }, useDocumentPlan: true);
        Assert.AreEqual(5, result.ChatCallCount); // Three graph calls, classifier, one repair for merged missing names.
        Assert.AreEqual(1, repairs);
        Assert.AreEqual(1, result.RelationshipCount);
        Assert.HasCount(2, result.EntityNames);
        invalidRepair = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = new string('x', 700) }, useDocumentPlan: true));
        Assert.AreEqual(2, repairs); // No second repair after a bad repair response.
        invalidRepair = false;
        unresolvedRepair = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = new string('x', 700) }, useDocumentPlan: true));
        Assert.AreEqual(3, repairs); // Declared unsupported names must not be accepted as repaired.
    }

    [TestMethod]
    public void ExtractionSections_PreserveSourceExactlyWithoutDuplicatedOverlap()
    {
        var content = "# Heading\r\n\r\nDo we keep punctuation?! Yes!\n" +
            string.Concat(Enumerable.Repeat("word 😀 ", 100)) + new string('x', 300);
        var sections = ExtractionDocumentPlan.Split(content, 64);
        Assert.AreEqual(content, string.Concat(sections));
        Assert.IsTrue(sections.All(section => section.Length is > 0 and <= 64));
        Assert.IsTrue(sections.All(section => !char.IsHighSurrogate(section[^1]) && !char.IsLowSurrogate(section[0])));
        Assert.IsEmpty(ExtractionDocumentPlan.Split("", 64));
    }

    [TestMethod]
    public void ClassificationEvidence_SamplesBothEndsOfLongDocuments()
    {
        var sections = Enumerable.Range(0, 100).Select(i => $"section-{i}:" + new string('x', 500)).ToArray();
        var evidence = ExtractionDocumentPlan.ClassificationEvidence(sections);
        StringAssert.Contains(evidence, "section-0:");
        StringAssert.Contains(evidence, "section-99:");
        Assert.IsLessThan(9_000, evidence.Length);
    }

    [TestMethod]
    public async Task DocumentPlan_ClassifiesLongDocumentOnceAndKeepsGraphCallsTaxonomyFree()
    {
        var prompts = new System.Collections.Concurrent.ConcurrentBag<string>();
        var client = new ScriptedChatClient((prompt, _, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(JsonResponse(prompt.Contains("document classification only") ? 99 : 1));
        });
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
                ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "1"
            });
        var source = Source with { ContentForIngestion = new string('x', 700) };
        var result = await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true);

        Assert.AreEqual(4, result.ChatCallCount); // Three graph sections, one classifier.
        Assert.AreEqual(3, result.ExtractionBatchCount);
        Assert.AreEqual(1, prompts.Count(p => p.Contains("document classification only")));
        Assert.AreEqual(3, prompts.Count(p => p.Contains("Do not classify domains or categories")));
        Assert.AreEqual("Domain-99", result.DomainName);
        Assert.HasCount(2, result.EntityNames); // Classifier cannot inject Entity-99 nodes.
        Assert.AreEqual(1, result.RelationshipCount);
    }

    [TestMethod]
    public async Task DocumentPlan_ShortDocumentUsesOneCombinedCall()
    {
        var client = new ScriptedChatClient((prompt, _, _) =>
        {
            StringAssert.Contains(prompt, "No existing domains");
            Assert.DoesNotContain("document classification only", prompt);
            return Task.FromResult(JsonResponse(1));
        });
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings());
        var result = await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "A uses B." },
            useDocumentPlan: true);
        Assert.AreEqual(1, result.ChatCallCount);
        Assert.AreEqual(1, result.RelationshipCount);
    }

    [TestMethod]
    public async Task DocumentPlan_RejectsIncompleteGraphInsteadOfCompletingPartialIngestion()
    {
        var client = new ScriptedChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))));
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractBatchesForTestingAsync(
            [], Source with { ContentForIngestion = "A uses B." }, useDocumentPlan: true));
    }

    [TestMethod]
    public async Task DocumentPlan_RejectsPartialRetryTreeAndDoesNotRepeatClassifier()
    {
        var classifications = 0;
        var client = new ScriptedChatClient((prompt, _, _) =>
        {
            if (prompt.Contains("document classification only"))
            {
                Interlocked.Increment(ref classifications);
                return Task.FromResult(JsonResponse(99));
            }
            return Task.FromResult(prompt.Contains(new string('b', 256))
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
                : JsonResponse(1));
        });
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            embeddings: new ZeroEmbeddings(), settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
                ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "2"
            });
        var source = Source with { ContentForIngestion = new string('a', 256) + new string('b', 256) + new string('c', 256) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true));
        Assert.AreEqual(1, classifications);
    }

    [TestMethod]
    public async Task DocumentPlan_RequiresModelUnlessExtractionExplicitlyDisabled()
    {
        var source = Source with { ContentForIngestion = "A uses B." };
        var enabled = CreateService(new AvailableModelsChatClientService(), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            enabled.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true));
        var disabled = CreateService(new AvailableModelsChatClientService(), false);
        var result = await disabled.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true);
        Assert.AreEqual(0, result.ChatCallCount);
    }

    [TestMethod]
    public async Task DocumentPlan_ReducesPromptVolumeForRepresentativeLongDocument()
    {
        var oldChars = 0;
        var newChars = 0;
        var oldClient = new ScriptedChatClient((prompt, _, _) =>
        {
            Interlocked.Add(ref oldChars, prompt.Length);
            return Task.FromResult(JsonResponse(1));
        });
        var newClient = new ScriptedChatClient((prompt, _, _) =>
        {
            Interlocked.Add(ref newChars, prompt.Length);
            return Task.FromResult(JsonResponse(1));
        });
        var source = Source with
        {
            ContentForIngestion = string.Concat(Enumerable.Range(0, 100)
                .Select(i => $"Paragraph {i:D3}: " + new string('x', 380) + "\n\n"))
        };
        var oldService = CreateService(new AvailableModelsChatClientService(oldClient, "graphrag"), true,
            embeddings: new ZeroEmbeddings());
        var newService = CreateService(new AvailableModelsChatClientService(newClient, "graphrag"), true,
            embeddings: new ZeroEmbeddings());
        var oldResult = await oldService.ExtractBatchesForTestingAsync(
            GraphRagPluginBase.SplitIntoChunks(source.ContentForIngestion, 500, 100), source);
        var newResult = await newService.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true);
        Assert.IsLessThan(oldChars, newChars);
        Assert.IsLessThanOrEqualTo(oldResult.ChatCallCount, newResult.ChatCallCount);
        Console.WriteLine($"Synthetic prompt comparison: legacy={oldChars} chars/{oldResult.ChatCallCount} calls; document plan={newChars} chars/{newResult.ChatCallCount} calls.");
    }

    [TestMethod]
    public void ExtractionBatching_SplitsSupplied108ChunkShapeAtConfiguredChunkLimit()
    {
        var chunks = Enumerable.Range(0, 108)
            .Select(index => $"chunk-{index:D3} " + new string('x', 488))
            .ToArray();

        var batchSizes = KnowledgeIngestionService.GetExtractionBatchSizesForTesting(
            chunks,
            Source,
            [],
            [],
            inputTokenBudget: 32_000);

        CollectionAssert.AreEqual(new[] { 32, 32, 32, 12 }, batchSizes.ToArray());
    }

    [TestMethod]
    public void ExtractionBatching_PreservesEveryChunkWhenBudgetRequiresSplitting()
    {
        var chunks = Enumerable.Range(0, 108)
            .Select(index => $"chunk-{index:D3} " + new string('x', 488))
            .ToArray();

        var batchSizes = KnowledgeIngestionService.GetExtractionBatchSizesForTesting(
            chunks,
            Source,
            [("Operations", "Operational knowledge")],
            [("Runbooks", "Operations", "Operational runbooks")],
            inputTokenBudget: 2_000,
            extractionInstructions: "Preserve every explicit dependency and version reference.");

        Assert.IsGreaterThan(1, batchSizes.Count);
        Assert.AreEqual(chunks.Length, batchSizes.Sum());
        Assert.IsTrue(batchSizes.All(size => size > 0));
    }

    [TestMethod]
    public void ExtractionBatching_UsesWhicheverOfChunkAndTokenLimitsIsReachedFirst()
    {
        var empty = KnowledgeIngestionService.GetExtractionBatchSizesForTesting(
            [], Source, [], [], inputTokenBudget: 32_000, maxChunksPerBatch: 2);
        Assert.IsEmpty(empty);

        var small = KnowledgeIngestionService.GetExtractionBatchSizesForTesting(
            ["one", "two", "three"], Source, [], [],
            inputTokenBudget: 32_000, maxChunksPerBatch: 2);
        CollectionAssert.AreEqual(new[] { 2, 1 }, small.ToArray());

        var taxonomy = Enumerable.Range(0, 150)
            .Select(index => ($"Domain-{index}", (string?)new string('d', 80)))
            .ToArray();
        var taxonomyHeavy = KnowledgeIngestionService.GetExtractionBatchSizesForTesting(
            [new string('x', 3_000), new string('y', 3_000)],
            Source,
            taxonomy,
            [],
            inputTokenBudget: 4_000,
            maxChunksPerBatch: 32);
        CollectionAssert.AreEqual(new[] { 1, 1 }, taxonomyHeavy.ToArray());
    }

    [TestMethod]
    public async Task EmbeddingBatching_UsesConfiguredBatchSizeAndPreservesOrder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GraphRagDb"] = "Server=unused;Database=unused;Integrated Security=true;",
                ["GraphRag:Ingestion:EmbeddingBatchSize"] = "128"
            })
            .Build();
        var embeddings = new CountingEmbeddings();
        var audit = new GraphRagAuditLog(
            configuration,
            NullLogger<GraphRagAuditLog>.Instance,
            "GraphRagDb");
        var service = new KnowledgeIngestionService(
            configuration,
            NullLogger<KnowledgeIngestionService>.Instance,
            "GraphRagDb",
            audit,
            embeddings);
        var inputs = Enumerable.Range(0, 260).Select(index => $"text-{index}").ToArray();

        var (results, batchCount) = await service.GenerateEmbeddingsBatchedForTestingAsync(inputs);

        Assert.AreEqual(3, batchCount);
        Assert.AreEqual(3, embeddings.BatchCallCount);
        Assert.AreEqual(0, embeddings.SingleCallCount);
        Assert.HasCount(inputs.Length, results);
        for (var index = 0; index < inputs.Length; index++)
            Assert.AreEqual(index, results[index]![0]);
    }

    [TestMethod]
    public async Task EmbeddingBatching_FallsBackPerItemWhenProviderBatchFails()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GraphRagDb"] = "Server=unused;Database=unused;Integrated Security=true;",
                ["GraphRag:Ingestion:EmbeddingBatchSize"] = "128",
                ["GraphRag:Ingestion:MaxEmbeddingConcurrency"] = "4"
            })
            .Build();
        var embeddings = new FailingBatchEmbeddings();
        var audit = new GraphRagAuditLog(
            configuration,
            NullLogger<GraphRagAuditLog>.Instance,
            "GraphRagDb");
        var service = new KnowledgeIngestionService(
            configuration,
            NullLogger<KnowledgeIngestionService>.Instance,
            "GraphRagDb",
            audit,
            embeddings);
        var inputs = Enumerable.Range(0, 140).Select(index => $"text-{index}").ToArray();

        var (results, batchCount) = await service.GenerateEmbeddingsBatchedForTestingAsync(inputs);

        Assert.AreEqual(2, batchCount);
        Assert.AreEqual(2, embeddings.BatchCallCount);
        Assert.AreEqual(inputs.Length, embeddings.SingleCallCount);
        for (var index = 0; index < inputs.Length; index++)
            Assert.AreEqual(index, results[index]![0]);
    }

    [TestMethod]
    public async Task EmbeddingBatching_RemotePartialResponseRetriesOnlyMissingVector()
    {
        var client = System.Reflection.DispatchProxy.Create<IFabrCoreHostApiClient, PartialEmbeddingProxy>();
        var proxy = (PartialEmbeddingProxy)client;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:GraphRagDb"] = "Server=unused;Database=unused;Integrated Security=true;"
            }).Build();
        var service = new KnowledgeIngestionService(configuration,
            NullLogger<KnowledgeIngestionService>.Instance, "GraphRagDb",
            new GraphRagAuditLog(configuration, NullLogger<GraphRagAuditLog>.Instance, "GraphRagDb"),
            hostApiClient: client);

        var result = await service.GenerateEmbeddingsBatchedForTestingAsync(["a", "bb", "ccc", "a"]);

        CollectionAssert.AreEqual(new float[] { 1, 2, 3, 1 }, result.Results.Select(x => x![0]).ToArray());
        CollectionAssert.AreEqual(new[] { "bb" }, proxy.Retried.ToArray());
        Assert.AreEqual(1, result.BatchCount);
    }

    [TestMethod]
    public async Task EmbeddingBatching_DeduplicatesInputsAndRestoresEveryPosition()
    {
        var embeddings = new CountingEmbeddings();
        var service = CreateService(new AvailableModelsChatClientService(), false,
            embeddings: embeddings, settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:EmbeddingBatchSize"] = "2"
            });

        var (results, batches) = await service.GenerateEmbeddingsBatchedForTestingAsync(
            ["text-2", "text-1", "text-2", "text-3", "text-1"]);

        Assert.AreEqual(2, batches);
        CollectionAssert.AreEqual(new float[] { 2, 1, 2, 3, 1 }, results.Select(x => x![0]).ToArray());
        Assert.AreEqual(0, embeddings.SingleCallCount);
    }

    [TestMethod]
    public async Task EmbeddingBatching_ParallelizesBatchesWithSharedLimitAcrossOperations()
    {
        var embeddings = new GatedEmbeddings();
        var service = CreateService(new AvailableModelsChatClientService(), false,
            embeddings: embeddings, settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:EmbeddingBatchSize"] = "1",
                ["GraphRag:Ingestion:MaxEmbeddingConcurrency"] = "2"
            });
        var first = service.GenerateEmbeddingsBatchedForTestingAsync(["a", "bb", "ccc", "dddd"]);
        try
        {
            // A sequential implementation cannot reach two active calls here.
            await embeddings.TwoActive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.GenerateEmbeddingsBatchedForTestingAsync(["eeeee", "ffffff"]);
            embeddings.Release.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.AreEqual(2, embeddings.MaxActive);
            CollectionAssert.AreEqual(new float[] { 1, 2, 3, 4 },
                first.Result.Results.Select(x => x![0]).ToArray());
            CollectionAssert.AreEqual(new float[] { 5, 6 },
                second.Result.Results.Select(x => x![0]).ToArray());
        }
        finally
        {
            embeddings.Release.TrySetResult();
            await first;
        }
    }

    [TestMethod]
    public async Task EmbeddingBatching_CancellationWhileQueuedDoesNotLeakPermits()
    {
        var embeddings = new GatedEmbeddings();
        var service = CreateService(new AvailableModelsChatClientService(), false,
            embeddings: embeddings, settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:EmbeddingBatchSize"] = "1",
                ["GraphRag:Ingestion:MaxEmbeddingConcurrency"] = "2"
            });
        var active = service.GenerateEmbeddingsBatchedForTestingAsync(["a", "bb"]);
        try
        {
            await embeddings.TwoActive.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();
            var queued = service.GenerateEmbeddingsBatchedForTestingAsync(["ccc"], cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        }
        finally
        {
            embeddings.Release.TrySetResult();
            await active;
        }
        var next = await service.GenerateEmbeddingsBatchedForTestingAsync(["dddd"]);
        Assert.AreEqual(4f, next.Results[0]![0]);
        Assert.AreEqual(2, embeddings.MaxActive);
    }

    [TestMethod]
    public async Task ModelResolution_PrefersGraphRagFallsBackToDefaultAndHonorsOptOut()
    {
        var preferredModels = new AvailableModelsChatClientService("graphrag", "default");
        var preferred = CreateService(preferredModels, enableExtraction: true);
        Assert.AreEqual("graphrag", await preferred.ResolveExtractionModelNameForTestingAsync());
        CollectionAssert.AreEqual(new[] { "graphrag" }, preferredModels.RequestedModels.ToArray());

        var fallbackModels = new AvailableModelsChatClientService("default");
        var fallback = CreateService(fallbackModels, enableExtraction: true);
        Assert.AreEqual("default", await fallback.ResolveExtractionModelNameForTestingAsync());
        CollectionAssert.AreEqual(new[] { "graphrag", "default" }, fallbackModels.RequestedModels.ToArray());

        var disabledModels = new AvailableModelsChatClientService("graphrag", "default");
        var disabled = CreateService(disabledModels, enableExtraction: false);
        Assert.IsNull(await disabled.ResolveExtractionModelNameForTestingAsync());
        Assert.IsEmpty(disabledModels.RequestedModels);

        var explicitModels = new AvailableModelsChatClientService("CustomFast", "graphrag", "default");
        var explicitService = CreateService(
            explicitModels,
            enableExtraction: true,
            extractionModelName: "customfast");
        Assert.AreEqual("customfast", await explicitService.ResolveExtractionModelNameForTestingAsync());
        Assert.AreEqual("customfast", await explicitService.ResolveExtractionModelNameForTestingAsync());
        CollectionAssert.AreEqual(new[] { "customfast" }, explicitModels.RequestedModels.ToArray());
    }

    [TestMethod]
    public async Task ChatConcurrency_DefaultLimitAllowsFourCallsWithoutSerializingAllDocuments()
    {
        var chatClient = new ConcurrencyTrackingChatClient();
        var models = new AvailableModelsChatClientService(chatClient, "graphrag");
        var service = CreateService(models, enableExtraction: true);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => service.GetChatCompletionForTestingAsync($"prompt-{index}")));

        Assert.AreEqual(4, chatClient.MaxObservedConcurrency);
    }

    [TestMethod]
    public async Task ExtractionMaxOutputTokens_IsPassedToLocalChatOptions()
    {
        var chatClient = new ScriptedChatClient((_, _, _) => Task.FromResult(JsonResponse(1)));
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:ExtractionMaxOutputTokens"] = "2048"
            });

        await service.GetChatCompletionForTestingAsync("prompt");

        Assert.IsNotNull(chatClient.LastOptions);
        Assert.AreEqual(2048, chatClient.LastOptions.MaxOutputTokens);
    }

    [TestMethod]
    public async Task ExtractionConcurrency_Processes108ChunksInFourCallsAndMergesBySourceOrder()
    {
        var chatClient = new ScriptedChatClient(async (prompt, callIndex, ct) =>
        {
            var section = ParseSection(prompt);
            await Task.Delay((5 - section) * 100, ct); // Complete in reverse source order.
            return JsonResponse(section);
        });
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings());
        var chunks = Enumerable.Range(0, 108)
            .Select(index => $"chunk-{index:D3} " + new string('x', 488))
            .ToArray();

        var result = await service.ExtractBatchesForTestingAsync(chunks, Source);

        Assert.AreEqual(4, result.ChatCallCount);
        Assert.AreEqual(4, result.ExtractionBatchCount);
        Assert.AreEqual(0, result.ExtractionRetryCount);
        Assert.AreEqual(0, result.ExtractionTruncationCount);
        Assert.AreEqual(4, chatClient.MaxObservedConcurrency);
        Assert.AreEqual("Domain-1", result.DomainName);
        CollectionAssert.AreEqual(
            new[] { "Entity-1-A", "Entity-1-B", "Entity-2-A", "Entity-2-B", "Entity-3-A", "Entity-3-B", "Entity-4-A", "Entity-4-B" },
            result.EntityNames.ToArray());
        Assert.AreEqual(4, result.RelationshipCount);
        Assert.IsLessThanOrEqualTo(
            600L,
            result.LlmExtractionMs,
            $"Concurrent extraction took {result.LlmExtractionMs}ms; expected no more than 1.5x the 400ms slowest batch.");
    }

    [TestMethod]
    public async Task ExtractionRetry_SplitsOnlyMalformedBatchAndPreservesSuccessfulChildren()
    {
        var chatClient = new ScriptedChatClient((prompt, _, _) =>
        {
            var containsFirst = prompt.Contains("chunk-first", StringComparison.Ordinal);
            var containsSecond = prompt.Contains("chunk-second", StringComparison.Ordinal);
            if (containsFirst && containsSecond)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{ malformed")));

            var suffix = containsFirst ? "First" : "Second";
            return Task.FromResult(JsonResponse(1, suffix));
        });
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings());

        var result = await service.ExtractBatchesForTestingAsync(
            new[] { "chunk-first", "chunk-second" },
            Source);

        Assert.AreEqual(3, result.ChatCallCount);
        Assert.AreEqual(3, result.ExtractionBatchCount);
        Assert.AreEqual(2, result.ExtractionRetryCount);
        Assert.AreEqual(1, result.ExtractionTruncationCount);
        CollectionAssert.AreEqual(
            new[] { "Entity-First-A", "Entity-First-B", "Entity-Second-A", "Entity-Second-B" },
            result.EntityNames.ToArray());
        Assert.AreEqual(2, result.RelationshipCount);
    }

    [TestMethod]
    public async Task ExtractionRetry_StopsAtConfiguredDepth()
    {
        var chatClient = new ScriptedChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not-json"))));
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings(),
            settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:MaxExtractionRetryDepth"] = "2"
            });

        var result = await service.ExtractBatchesForTestingAsync(
            new[] { "one", "two", "three", "four" },
            Source);

        Assert.AreEqual(7, result.ChatCallCount);
        Assert.AreEqual(7, result.ExtractionBatchCount);
        Assert.AreEqual(6, result.ExtractionRetryCount);
        Assert.AreEqual(7, result.ExtractionTruncationCount);
        Assert.IsEmpty(result.EntityNames);
    }

    [TestMethod]
    public async Task ExtractionRetry_SplitsExplicitLengthLimitedResponse()
    {
        var chatClient = new ScriptedChatClient((prompt, callIndex, _) =>
        {
            if (callIndex == 1)
                return Task.FromResult(WithFinishReason(JsonResponse(1, "Parent"), "Length"));

            var suffix = prompt.Contains("first", StringComparison.Ordinal) ? "First" : "Second";
            return Task.FromResult(JsonResponse(1, suffix));
        });
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings());

        var result = await service.ExtractBatchesForTestingAsync(
            new[] { "first", "second" }, Source);

        Assert.AreEqual(3, result.ExtractionBatchCount);
        Assert.AreEqual(2, result.ExtractionRetryCount);
        Assert.AreEqual(1, result.ExtractionTruncationCount);
        Assert.HasCount(4, result.EntityNames);
    }

    [TestMethod]
    public async Task ExtractionRetry_DoesNotRetryProviderFailure()
    {
        var chatClient = new ScriptedChatClient((_, _, _) =>
            throw new InvalidOperationException("Synthetic authentication failure."));
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings());

        var result = await service.ExtractBatchesForTestingAsync(new[] { "content" }, Source);

        Assert.AreEqual(1, result.ExtractionBatchCount);
        Assert.AreEqual(0, result.ExtractionRetryCount);
        Assert.AreEqual(0, result.ExtractionTruncationCount);
        Assert.IsEmpty(result.EntityNames);
    }

    [TestMethod]
    public async Task ExtractionRetry_RetriesEmptyResponseButNotContentFilter()
    {
        var emptyClient = new ScriptedChatClient((prompt, callIndex, _) =>
        {
            if (callIndex == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

            var suffix = prompt.Contains("first", StringComparison.Ordinal) ? "First" : "Second";
            return Task.FromResult(JsonResponse(1, suffix));
        });
        var emptyService = CreateService(
            new AvailableModelsChatClientService(emptyClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings());

        var recovered = await emptyService.ExtractBatchesForTestingAsync(
            new[] { "first", "second" }, Source);

        Assert.AreEqual(3, recovered.ExtractionBatchCount);
        Assert.AreEqual(2, recovered.ExtractionRetryCount);
        Assert.AreEqual(1, recovered.ExtractionTruncationCount);

        var filteredClient = new ScriptedChatClient((_, _, _) => Task.FromResult(
            WithFinishReason(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{ filtered")),
                "ContentFilter")));
        var filteredService = CreateService(
            new AvailableModelsChatClientService(filteredClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings());

        var filtered = await filteredService.ExtractBatchesForTestingAsync(
            new[] { "first", "second" }, Source);

        Assert.AreEqual(1, filtered.ExtractionBatchCount);
        Assert.AreEqual(0, filtered.ExtractionRetryCount);
        Assert.AreEqual(0, filtered.ExtractionTruncationCount);
    }

    [TestMethod]
    public async Task ExtractionMerge_DeduplicatesEntitiesAndRelationshipsCaseInsensitively()
    {
        var chatClient = new ScriptedChatClient((_, callIndex, _) =>
        {
            var label = callIndex == 1 ? "Shared" : "shared";
            return Task.FromResult(JsonResponse(1, label));
        });
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings(),
            settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:MaxChunksPerExtractionBatch"] = "1"
            });

        var result = await service.ExtractBatchesForTestingAsync(
            new[] { "first", "second" }, Source);

        Assert.HasCount(2, result.EntityNames);
        Assert.AreEqual(1, result.RelationshipCount);
    }

    [TestMethod]
    public async Task ExtractionMetrics_AggregateImmutablePerCallResults()
    {
        var chatClient = new ScriptedChatClient((_, callIndex, _) =>
        {
            var response = WithFinishReason(JsonResponse(callIndex), "Stop");
            response.Usage = new UsageDetails
            {
                InputTokenCount = 100 + callIndex,
                OutputTokenCount = 10 + callIndex
            };
            return Task.FromResult(response);
        });
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings(),
            settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:MaxChunksPerExtractionBatch"] = "1"
            });

        var result = await service.ExtractBatchesForTestingAsync(
            new[] { "first", "second" }, Source);

        Assert.AreEqual(203L, result.ChatInputTokens);
        Assert.AreEqual(23L, result.ChatOutputTokens);
        Assert.IsGreaterThanOrEqualTo(0L, result.ChatTotalMs);
        Assert.AreEqual("Test", result.ResolvedProviderName);
        Assert.AreEqual("test-model", result.ResolvedDeploymentModelName);
        CollectionAssert.AreEqual(new[] { "stop" }, result.FinishReasons.ToArray());
    }

    [TestMethod]
    public async Task ExtractionConcurrency_PropagatesCancellation()
    {
        var chatClient = new ScriptedChatClient(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return JsonResponse(1);
        });
        var service = CreateService(
            new AvailableModelsChatClientService(chatClient, "graphrag"),
            enableExtraction: true,
            embeddings: new ZeroEmbeddings());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExtractBatchesForTestingAsync(new[] { "content" }, Source, cts.Token));
    }

    [TestMethod]
    public void ExtractionResponse_CompactAndLegacyRelationshipsParseIdentically()
    {
        const string compact = """
            {"domain":null,"category":null,"entities":[],"relationships":[{"from":"A","to":"B","type":"USES","description":"uses","confidence":0.9}]}
            """;
        const string legacy = """
            {"domain":null,"category":null,"entities":[],"relationships":[{"from":"A","fromType":"System","to":"B","toType":"Technology","type":"USES","description":"uses","confidence":0.9}]}
            """;

        var compactSummary = KnowledgeIngestionService.ParseExtractionSummaryForTesting(compact, "Markdown");
        var legacySummary = KnowledgeIngestionService.ParseExtractionSummaryForTesting(legacy, "Markdown");
        var prompt = KnowledgeIngestionService.BuildExtractionPromptForTesting(
            new[] { "content" }, Source, [], []);

        Assert.IsNotNull(compactSummary);
        Assert.IsNotNull(legacySummary);
        Assert.AreEqual(compactSummary.RelationshipCount, legacySummary.RelationshipCount);
        Assert.DoesNotContain("\"fromType\"", prompt);
        Assert.DoesNotContain("\"toType\"", prompt);
    }

    [TestMethod]
    public async Task ResultCache_ReusesOnlyExactValidatedGraphBatchesAndCountsActualCalls()
    {
        var cache = new ExtractionResultCache();
        var malformed = false;
        var client = new ScriptedChatClient((_, _, _) => Task.FromResult(malformed
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "invalid")) : JsonResponse(1)));
        var settings = new Dictionary<string, string?>
        {
            ["GraphRag:Ingestion:UseExtractionResultCache"] = "true",
            ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
            ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "1",
            ["GraphRag:Ingestion:UseExtractionJsonSchema"] = "true"
        };
        var models = new AvailableModelsChatClientService(client, "graphrag", "other");
        var service = CreateService(models, true, settings: settings, resultCache: cache);
        var source = Source with { ContentForIngestion = new string('x', 700) };
        var cold = await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true);
        var warm = await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true);
        Assert.AreEqual(4, cold.ChatCallCount);
        Assert.AreEqual(1, warm.ChatCallCount); // Classification remains live.
        CollectionAssert.AreEqual(cold.EntityNames.ToArray(), warm.EntityNames.ToArray());
        Assert.AreEqual(cold.RelationshipCount, warm.RelationshipCount);
        Assert.AreEqual(3L, cache.Hits);
        var sameService = CreateService(models, true, settings: settings, resultCache: cache);
        Assert.AreEqual(1, (await sameService.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
        var otherModel = CreateService(models, true, extractionModelName: "other", settings: settings, resultCache: cache);
        Assert.AreEqual(4, (await otherModel.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
        var edit = await service.ExtractBatchesForTestingAsync([], source with { ContentForIngestion = new string('x', 699) + "y" }, useDocumentPlan: true);
        Assert.AreEqual(2, edit.ChatCallCount);
        var isolated = await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true, documentId: Guid.NewGuid());
        Assert.AreEqual(4, isolated.ChatCallCount);
        settings["GraphRag:Ingestion:UseExtractionJsonSchema"] = "false";
        var changed = CreateService(models, true, settings: settings, resultCache: cache);
        Assert.AreEqual(4, (await changed.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
        malformed = true;
        var badSource = source with { ContentForIngestion = new string('z', 700) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractBatchesForTestingAsync([], badSource, useDocumentPlan: true));
        malformed = false;
        Assert.AreEqual(4, (await service.ExtractBatchesForTestingAsync([], badSource, useDocumentPlan: true)).ChatCallCount);
    }

    [TestMethod]
    public async Task TaxonomyCache_InvalidatesCombinedAndClassifierOnContextChanges()
    {
        foreach (var length in new[] { 100, 700 })
        {
            var client = new ScriptedChatClient((_, _, _) => Task.FromResult(JsonResponse(1)));
            var settings = new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionResultCache"] = "true",
                ["GraphRag:Ingestion:CacheTaxonomyResponses"] = "true",
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256",
                ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = "1",
                ["GraphRag:Ingestion:UseExtractionJsonSchema"] = "true"
            };
            var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true, settings: settings);
            var source = Source with { ContentForIngestion = new string('x', length) };
            var cold = await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true);
            var warm = await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true);
            Assert.AreEqual(length == 100 ? 1 : 4, cold.ChatCallCount);
            Assert.AreEqual(0, warm.ChatCallCount);
            Assert.AreEqual(0L, warm.ChatInputTokens);
            Assert.AreEqual(0L, warm.ChatOutputTokens);
            CollectionAssert.AreEqual(cold.EntityNames.ToArray(), warm.EntityNames.ToArray());
            Assert.AreEqual(cold.DomainName, warm.DomainName);
            Assert.AreEqual(cold.RelationshipCount, warm.RelationshipCount);
            foreach (var context in new[]
            {
                ("Domain", "description", "Category", "Domain", "category description"),
                ("Domain", "changed", "Category", "Domain", "category description"),
                ("Domain", "changed", "Category", "Other domain", "category description"),
                ("Domain", "changed", "Category", "Other domain", "changed category")
            })
            {
                var changed = await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true,
                    domains: [(context.Item1, context.Item2)], categories: [(context.Item3, context.Item4, context.Item5)]);
                Assert.AreEqual(1, changed.ChatCallCount); // Long-document graph-only batches still reuse.
                Assert.AreEqual(0, (await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true,
                    domains: [(context.Item1, context.Item2)], categories: [(context.Item3, context.Item4, context.Item5)])).ChatCallCount);
            }
            // An exact earlier context remains reusable, with no stale cross-context reuse.
            Assert.AreEqual(0, (await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
            Assert.AreEqual(length == 100 ? 1 : 4, (await service.ExtractBatchesForTestingAsync([], source,
                useDocumentPlan: true, instructions: "changed guidance")).ChatCallCount);
            Assert.AreEqual(length == 100 ? 1 : 2, (await service.ExtractBatchesForTestingAsync([], source with
                { ContentForIngestion = new string('x', length - 1) + "y" }, useDocumentPlan: true)).ChatCallCount);
        }
    }

    [TestMethod]
    public async Task TaxonomyCache_DoesNotStoreCombinedResponsesMissingClassification()
    {
        var client = new ScriptedChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "{\"entities\":[],\"relationships\":[]}"))));
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionResultCache"] = "true",
                ["GraphRag:Ingestion:CacheTaxonomyResponses"] = "true"
            });
        var source = Source with { ContentForIngestion = "Short content" };
        Assert.AreEqual(1, (await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
        Assert.AreEqual(1, (await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
    }

    [TestMethod]
    public async Task TaxonomyCache_InvalidatesEvenWhenEditedSourceIsOutsideClassifierSample()
    {
        var content = new string('x', 10_240);
        var edited = content[..255] + "y" + content[256..];
        Assert.AreEqual(ExtractionDocumentPlan.ClassificationEvidence(ExtractionDocumentPlan.Split(content, 256)),
            ExtractionDocumentPlan.ClassificationEvidence(ExtractionDocumentPlan.Split(edited, 256)));
        var client = new ScriptedChatClient((_, _, _) => Task.FromResult(JsonResponse(1)));
        var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
            settings: new Dictionary<string, string?>
            {
                ["GraphRag:Ingestion:UseExtractionResultCache"] = "true",
                ["GraphRag:Ingestion:CacheTaxonomyResponses"] = "true",
                ["GraphRag:Ingestion:ExtractionSectionSizeChars"] = "256"
            });
        var source = Source with { ContentForIngestion = content };
        Assert.AreEqual(6, (await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
        Assert.AreEqual(0, (await service.ExtractBatchesForTestingAsync([], source, useDocumentPlan: true)).ChatCallCount);
        Assert.AreEqual(2, (await service.ExtractBatchesForTestingAsync([], source with { ContentForIngestion = edited }, useDocumentPlan: true)).ChatCallCount);
    }

    [TestMethod]
    public async Task EndpointAliases_ResolveBeforePersistenceWithoutMoreLlmCalls()
    {
        const string json = """
            {"domain":{"name":"Standards","description":"Standards","isNew":true,"confidence":0.9},
            "category":{"name":"Formats","description":"Formats","isNew":true,"confidence":0.9},
            "entities":[{"name":"Internet Assigned Numbers Authority (IANA)","entityType":"Organization","description":"Registers media types"},
            {"name":"text/csv","entityType":"Format","description":"CSV media type"}],
            "relationships":[{"from":"IANA","to":"text/csv","type":"ESTABLISHES","description":"Registers","confidence":0.99}]}
            """;
        foreach (var enabled in new[] { false, true })
        {
            var client = new ScriptedChatClient((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));
            var service = CreateService(new AvailableModelsChatClientService(client, "graphrag"), true,
                settings: new Dictionary<string, string?> { ["GraphRag:Ingestion:ResolveExtractionEndpointAliases"] = enabled.ToString() });
            var result = await service.ExtractBatchesForTestingAsync([], Source with { ContentForIngestion = "IANA registered text/csv." }, useDocumentPlan: true);
            Assert.AreEqual(1, result.ChatCallCount);
            Assert.AreEqual(enabled ? "Internet Assigned Numbers Authority (IANA)" : "IANA", result.RelationshipEndpoints.Single().From);
            Assert.AreEqual("text/csv", result.RelationshipEndpoints.Single().To);
            Assert.HasCount(2, result.EntityNames);
        }
    }

    private static KnowledgeIngestionService CreateService(
        IFabrCoreChatClientService chatClientService,
        bool enableExtraction,
        string? extractionModelName = null,
        IEmbeddings? embeddings = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        ExtractionResultCache? resultCache = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:GraphRagDb"] = "Server=unused;Database=unused;Integrated Security=true;",
            ["GraphRag:Ingestion:EnableExtraction"] = enableExtraction.ToString()
        };
        if (settings is not null)
        {
            foreach (var setting in settings)
                values[setting.Key] = setting.Value;
        }
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var audit = new GraphRagAuditLog(
            configuration,
            NullLogger<GraphRagAuditLog>.Instance,
            "GraphRagDb");
        var provider = new ServiceCollection()
            .AddSingleton(resultCache ?? new ExtractionResultCache())
            .AddSingleton(chatClientService)
            .BuildServiceProvider();
        return new KnowledgeIngestionService(
            configuration,
            NullLogger<KnowledgeIngestionService>.Instance,
            "GraphRagDb",
            audit,
            embeddings: embeddings,
            serviceProvider: provider,
            extractionModelName: extractionModelName);
    }

    private static int ParseSection(string prompt)
    {
        const string marker = "Section ";
        var start = prompt.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = prompt.IndexOf(' ', start);
        return int.Parse(prompt.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ChatResponse JsonResponse(int section, string? suffix = null)
    {
        var label = suffix ?? section.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var json = $$"""
            {
              "domain":{"name":"Domain-{{section}}","description":"domain","isNew":true,"confidence":0.9},
              "category":{"name":"Category-{{section}}","description":"category","isNew":true,"confidence":0.9},
              "entities":[
                {"name":"Entity-{{label}}-A","entityType":"Concept","description":"A"},
                {"name":"Entity-{{label}}-B","entityType":"Technology","description":"B"}
              ],
              "relationships":[{"from":"Entity-{{label}}-A","to":"Entity-{{label}}-B","type":"USES","description":"uses","confidence":0.9}]
            }
            """;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
    }

    private static ChatResponse WithFinishReason(ChatResponse response, string finishReason)
    {
        var property = response.GetType().GetProperty("FinishReason")
            ?? throw new AssertFailedException("ChatResponse.FinishReason is unavailable.");
        var finishReasonType = Nullable.GetUnderlyingType(property.PropertyType)
            ?? property.PropertyType;
        var value = finishReasonType.GetProperty(
                finishReason,
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.IgnoreCase)
            ?.GetValue(null)
            ?? throw new AssertFailedException($"Chat finish reason '{finishReason}' is unavailable.");
        property.SetValue(response, value);
        return response;
    }

    public class PartialEmbeddingProxy : System.Reflection.DispatchProxy
    {
        public List<string> Retried { get; } = [];

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IFabrCoreHostApiClient.GetBatchEmbeddingsAsync))
            {
                var items = (List<BatchEmbeddingItem>)args![0]!;
                return Task.FromResult(new BatchEmbeddingResponse
                {
                    Results = items.Where(item => item.Text != "bb").Reverse().Select(item =>
                        new BatchEmbeddingResultItem { Id = item.Id, Vector = [item.Text.Length] }).ToList()
                });
            }
            if (targetMethod.Name == nameof(IFabrCoreHostApiClient.GetEmbeddingsAsync))
            {
                var text = (string)args![0]!;
                Retried.Add(text);
                return Task.FromResult(new EmbeddingResponse { Vector = [text.Length] });
            }
            throw new AssertFailedException($"Unexpected call: {targetMethod.Name}");
        }
    }

    private sealed class GatedEmbeddings : IEmbeddings
    {
        private int _active;
        private int _maxActive;
        public int MaxActive => Volatile.Read(ref _maxActive);
        public TaskCompletionSource TwoActive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Embedding<float>> GetEmbeddings(string text) => throw new AssertFailedException("Unexpected fallback");

        public async Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maxActive, active);
            if (active == 2) TwoActive.TrySetResult();
            try
            {
                await Release.Task;
                return texts.Select(text => new Embedding<float>(new float[] { text.Length })).ToArray();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class CountingEmbeddings : IEmbeddings
    {
        private int _batchCallCount;
        private int _singleCallCount;
        public int BatchCallCount => Volatile.Read(ref _batchCallCount);
        public int SingleCallCount => Volatile.Read(ref _singleCallCount);

        public Task<Embedding<float>> GetEmbeddings(string text)
        {
            Interlocked.Increment(ref _singleCallCount);
            return Task.FromResult(new Embedding<float>(new float[] { ParseIndex(text) }));
        }

        public Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
        {
            Interlocked.Increment(ref _batchCallCount);
            return Task.FromResult<IReadOnlyList<Embedding<float>>>(
                texts.Select(text => new Embedding<float>(new float[] { ParseIndex(text) })).ToArray());
        }

        private static float ParseIndex(string text)
            => float.Parse(text.AsSpan("text-".Length), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FailingBatchEmbeddings : IEmbeddings
    {
        private int _batchCallCount;
        public int BatchCallCount => Volatile.Read(ref _batchCallCount);
        public int SingleCallCount => Volatile.Read(ref _singleCallCount);

        public Task<Embedding<float>> GetEmbeddings(string text)
        {
            Interlocked.Increment(ref _singleCallCount);
            return Task.FromResult(new Embedding<float>(new float[] { ParseIndex(text) }));
        }

        private int _singleCallCount;

        Task<IReadOnlyList<Embedding<float>>> IEmbeddings.GetBatchEmbeddings(IReadOnlyList<string> texts)
        {
            Interlocked.Increment(ref _batchCallCount);
            throw new InvalidOperationException("Synthetic provider batch failure.");
        }

        private static float ParseIndex(string text)
            => float.Parse(text.AsSpan("text-".Length), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class AvailableModelsChatClientService : IFabrCoreChatClientService
    {
        private readonly HashSet<string> _available;
        private readonly IChatClient _chatClient;

        public AvailableModelsChatClientService(params string[] available)
            : this(new StubChatClient(), available)
        {
        }

        public AvailableModelsChatClientService(IChatClient chatClient, params string[] available)
        {
            _chatClient = chatClient;
            _available = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public List<string> RequestedModels { get; } = [];

        public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
            => _available.Contains(name)
                ? Task.FromResult(_chatClient)
                : throw new InvalidOperationException($"Model '{name}' is unavailable.");

#pragma warning disable MEAI001
        public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
            => throw new NotSupportedException();
#pragma warning restore MEAI001

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
            => throw new NotSupportedException();

        public Task<ModelConfiguration> GetModelConfigurationAsync(string name)
        {
            RequestedModels.Add(name);
            return _available.Contains(name)
                ? Task.FromResult(new ModelConfiguration
                {
                    Name = name,
                    Provider = "Test",
                    Uri = "https://test.invalid",
                    Model = "test-model",
                    ApiKeyAlias = "test",
                    TimeoutSeconds = 30,
                    ContextWindowTokens = 128_000
                })
                : throw new InvalidOperationException($"Model '{name}' is unavailable.");
        }
    }

    private sealed class ZeroEmbeddings : IEmbeddings
    {
        public Task<Embedding<float>> GetEmbeddings(string text)
            => Task.FromResult(new Embedding<float>(new float[1536]));

        public Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
            => Task.FromResult<IReadOnlyList<Embedding<float>>>(
                texts.Select(_ => new Embedding<float>(new float[1536])).ToArray());
    }

    private sealed class ScriptedChatClient(
        Func<string, int, CancellationToken, Task<ChatResponse>> responseFactory) : IChatClient
    {
        private int _active;
        private int _callCount;
        private int _maxObserved;

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);
        public ChatOptions? LastOptions { get; private set; }
        public Action<string, ChatOptions?>? Observe { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maxObserved, active);
            var callIndex = Interlocked.Increment(ref _callCount);
            try
            {
                var prompt = chatMessages.Last().Text;
                Observe?.Invoke(prompt, options);
                return await responseFactory(prompt, callIndex, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(chatMessages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "{}");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ConcurrencyTrackingChatClient : IChatClient
    {
        private int _active;
        private int _maxObserved;

        public int MaxObservedConcurrency => _maxObserved;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maxObserved, active);
            try
            {
                await Task.Delay(50, cancellationToken);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(chatMessages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
