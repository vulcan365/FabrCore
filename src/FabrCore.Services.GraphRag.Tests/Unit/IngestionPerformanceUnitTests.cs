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

    private static KnowledgeIngestionService CreateService(
        IFabrCoreChatClientService chatClientService,
        bool enableExtraction,
        string? extractionModelName = null,
        IEmbeddings? embeddings = null,
        IReadOnlyDictionary<string, string?>? settings = null)
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

    private sealed class CountingEmbeddings : IEmbeddings
    {
        public int BatchCallCount { get; private set; }
        public int SingleCallCount { get; private set; }

        public Task<Embedding<float>> GetEmbeddings(string text)
        {
            SingleCallCount++;
            return Task.FromResult(new Embedding<float>(new float[] { ParseIndex(text) }));
        }

        public Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
        {
            BatchCallCount++;
            return Task.FromResult<IReadOnlyList<Embedding<float>>>(
                texts.Select(text => new Embedding<float>(new float[] { ParseIndex(text) })).ToArray());
        }

        private static float ParseIndex(string text)
            => float.Parse(text.AsSpan("text-".Length), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FailingBatchEmbeddings : IEmbeddings
    {
        public int BatchCallCount { get; private set; }
        public int SingleCallCount => Volatile.Read(ref _singleCallCount);

        public Task<Embedding<float>> GetEmbeddings(string text)
        {
            Interlocked.Increment(ref _singleCallCount);
            return Task.FromResult(new Embedding<float>(new float[] { ParseIndex(text) }));
        }

        private int _singleCallCount;

        Task<IReadOnlyList<Embedding<float>>> IEmbeddings.GetBatchEmbeddings(IReadOnlyList<string> texts)
        {
            BatchCallCount++;
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
