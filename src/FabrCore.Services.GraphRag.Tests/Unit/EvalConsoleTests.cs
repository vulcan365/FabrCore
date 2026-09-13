using FabrCore.Services.GraphRag.EvalConsole;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class EvalConsoleTests
{
    [TestMethod]
    public async Task JsonNormalization_PreservesFactsUsageAndOriginalResponse()
    {
        const string raw = """{"entities":[[{"name":"RFC 8259"}]],"relationships":[[{"from":"A","to":"B"}]]}""";
        var normalized = JsonObjectChatClient.NormalizeArrays(raw)!;
        using var parsed = System.Text.Json.JsonDocument.Parse(normalized);
        Assert.AreEqual("RFC 8259", parsed.RootElement.GetProperty("entities")[0].GetProperty("name").GetString());
        Assert.AreEqual("B", parsed.RootElement.GetProperty("relationships")[0].GetProperty("to").GetString());
        Assert.IsNull(JsonObjectChatClient.NormalizeArrays("""{"entities":[["invalid"]]}"""));
        Assert.IsNull(JsonObjectChatClient.NormalizeArrays("""{"entities":[]} trailing"""));
        var usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 34 };
        var inner = new UsageClient { Response = new ChatResponse(new ChatMessage(ChatRole.Assistant, raw)) { Usage = usage } };
        var proxy = System.Reflection.DispatchProxy.Create<IFabrCoreChatClientService, ClientServiceProxy>();
        ((ClientServiceProxy)proxy).Client = inner;
        var service = new MeasuredChatClientService(proxy, captureResponses: true, useJsonObjectResponses: true,
            guideJsonObjectResponses: true, normalizeJsonArrays: true);
        var client = await service.GetChatClient("test");
        var response = await client.GetResponseAsync("Extract");
        Assert.AreSame(usage, response.Usage);
        Assert.AreEqual(raw, inner.Response.Text);
        var sample = service.Drain().Single();
        Assert.AreEqual(raw, sample.RawProviderResponseJson);
        Assert.AreEqual(normalized, sample.ExtractionResponseJson);
    }

    [TestMethod]
    public async Task GuidedJsonMode_PreservesMessagesAndRecordsExplicitGenerationChanges()
    {
        Assert.AreEqual("json-guided", Options.Parse(["run", "--response", "json-guided"]).Value("response", "prompt"));
        var inner = new UsageClient { Response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")) };
        using var client = new JsonObjectChatClient(inner, guided: true);
        var original = new ChatMessage(ChatRole.User, "Source text");
        var options = new ChatOptions { Temperature = 0.7f };
        await client.GetResponseAsync([original], options);
        Assert.AreEqual(0.7f, options.Temperature);
        Assert.AreEqual(0f, inner.LastOptions!.Temperature);
        Assert.AreEqual(ChatRole.System, inner.LastMessages[0].Role);
        Assert.AreEqual(JsonObjectChatClient.Guidance, inner.LastMessages[0].Text);
        Assert.AreSame(original, inner.LastMessages[1]);
    }

    [TestMethod]
    public async Task JsonObjectMode_PreservesCallerOptionsAndUsesNoSchema()
    {
        Assert.AreEqual("json", Options.Parse(["run", "--response", "json"]).Value("response", "prompt"));
        var inner = new UsageClient { Response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")) };
        using var client = new JsonObjectChatClient(inner);
        var options = new ChatOptions { MaxOutputTokens = 1024, Temperature = 0 };
        await client.GetResponseAsync("Extract JSON", options);
        Assert.IsNull(options.ResponseFormat);
        Assert.AreNotSame(options, inner.LastOptions);
        Assert.AreSame(ChatResponseFormat.Json, inner.LastOptions!.ResponseFormat);
        Assert.AreEqual(1024, inner.LastOptions.MaxOutputTokens);
        Assert.AreEqual(0f, inner.LastOptions.Temperature);
        await client.GetResponseAsync("Extract JSON");
        Assert.AreSame(ChatResponseFormat.Json, inner.LastOptions!.ResponseFormat);
    }

    [TestMethod]
    public async Task ReportCheckpoint_RetriesTemporaryWindowsSharingViolation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "graphrag-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var report = new EvalReport("retry", DateTimeOffset.UtcNow, "local", "eval", "model", "provider", "deployment", "embedding", []);
            await ReportWriter.WriteAsync(directory, report);
            Task retry;
            using (var locked = File.Open(Path.Combine(directory, "report.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                report.Completed = true;
                retry = ReportWriter.WriteAsync(directory, report);
                await Task.Delay(150);
                Assert.IsFalse(retry.IsCompleted);
            }
            await retry;
            using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "report.json")));
            Assert.IsTrue(json.RootElement.GetProperty("Completed").GetBoolean());
        }
        finally
        {
            foreach (var name in new[] { "report.json", "report.json.tmp", "report.md" }) File.Delete(Path.Combine(directory, name));
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public async Task ChatTelemetry_PreservesResponseAndDistinguishesMissingUsageFromZero()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, CachedInputTokenCount = 0, ReasoningTokenCount = 3 }
        };
        var client = new UsageClient { Response = response };
        var proxy = System.Reflection.DispatchProxy.Create<IFabrCoreChatClientService, ClientServiceProxy>();
        ((ClientServiceProxy)proxy).Client = client;
        var service = new MeasuredChatClientService(proxy);
        var measured = await service.GetChatClient("test-model");
        var options = new ChatOptions { MaxOutputTokens = 500 };
        Assert.AreSame(response, await measured.GetResponseAsync("prompt", options));
        Assert.AreSame(options, client.LastOptions);
        var first = service.Drain().Single();
        Assert.AreEqual(0L, first.CachedInputTokens);
        Assert.AreEqual(3L, first.ReasoningTokens);
        Assert.AreEqual(100L, first.InputTokens);
        Assert.IsEmpty(service.Drain());
        client.Response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));
        await measured.GetResponseAsync("prompt");
        Assert.IsNull(service.Drain().Single().CachedInputTokens);
        client.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => measured.GetResponseAsync("prompt"));
        var failed = service.Drain().Single();
        Assert.AreEqual(nameof(InvalidOperationException), failed.ErrorType);
        Assert.IsNull(failed.InputTokens);
    }

    [TestMethod]
    public async Task ConcurrentMeasurements_KeepRequestsWithTheirDocument_AndRestoreParent()
    {
        var proxy = System.Reflection.DispatchProxy.Create<IFabrCoreChatClientService, ClientServiceProxy>();
        ((ClientServiceProxy)proxy).Client = new OverlappingClient();
        var service = new MeasuredChatClientService(proxy, captureResponses: true);
        var client = await service.GetChatClient("test");
        using var parent = new DocumentMeasurements();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async index =>
        {
            using var scope = new DocumentMeasurements();
            await client.GetResponseAsync(index.ToString());
            return scope.ChatCalls.Single();
        }));
        CollectionAssert.AreEquivalent(new[] { "0", "1", "2" }, results.Select(r => r.ExtractionResponseJson).ToArray());
        Assert.AreEqual(3, service.PeakConcurrency);
        Assert.AreSame(parent, DocumentMeasurements.Current);
        Assert.IsEmpty(parent.ChatCalls);
        Assert.IsEmpty(service.Drain());
        service.ResetPeakConcurrency();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetResponseAsync("cancel", cancellationToken: cancel.Token));
        Assert.HasCount(1, parent.ChatCalls);
        Assert.AreEqual(1, service.PeakConcurrency);
    }

    [TestMethod]
    public void ConcurrentOptions_RejectInvalidLimitsAndUnattributableCacheCounters()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["run", "--document-concurrency", "0"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["run", "--document-concurrency", "2", "--embedding-cache", "on"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["run", "--document-concurrency", "2", "--result-cache", "on"]));
        Assert.AreEqual(3, Options.Parse(["run", "--document-concurrency", "3"]).Number("document-concurrency", 1, 1, 16));
    }

    private sealed class OverlappingClient : IChatClient
    {
        private int entered;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref entered) == 3) ready.SetResult();
            await ready.Task.WaitAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, messages.Single().Text));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [TestMethod]
    public async Task ConcurrentEmbeddingMeasurements_ExcludeWarmupAndKeepDocumentOwnership()
    {
        var measured = new MeasuredEmbeddings(new YieldingEmbeddings());
        await measured.GetEmbeddings("warmup");
        var samples = await Task.WhenAll(Enumerable.Range(1, 3).Select(async size =>
        {
            using var scope = new DocumentMeasurements();
            await measured.GetBatchEmbeddings([new string('x', size)]);
            return scope.EmbeddingCalls.Single();
        }));
        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, samples.Select(s => s.Characters).ToArray());
        Assert.AreEqual(6L, measured.Drain().Single().Characters);
    }

    private sealed class YieldingEmbeddings : IEmbeddings
    {
        public async Task<Embedding<float>> GetEmbeddings(string text)
        {
            await Task.Yield();
            return new Embedding<float>(new float[] { 1, 0 });
        }
        public async Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts)
            => await Task.WhenAll(texts.Select(GetEmbeddings));
    }

    public class ClientServiceProxy : System.Reflection.DispatchProxy
    {
        public IChatClient Client { get; set; } = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
            => method!.Name == nameof(IFabrCoreChatClientService.GetChatClient)
                ? Task.FromResult(Client) : throw new NotSupportedException();
    }

    private sealed class UsageClient : IChatClient
    {
        public ChatResponse Response { get; set; } = null!;
        public ChatOptions? LastOptions { get; private set; }
        public List<ChatMessage> LastMessages { get; private set; } = [];
        public bool Fail { get; set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            LastMessages = messages.ToList();
            return Fail ? throw new InvalidOperationException("Test failure") : Task.FromResult(Response);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static readonly CorpusEntry Entry = new("gold.txt", "https://example.invalid/gold.txt", "query", ["Gold"], ["answer"]);

    [TestMethod]
    public void RetrievalGate_DetectsRankEvidenceAndCrossScopeLeak()
    {
        var result = RetrievalResult.FromJson(Entry, "allowed", """
            [{"entityName":"other.txt","scope":"allowed","content":"noise"},
             {"entityName":"gold.txt","scope":"denied","content":"the answer"}]
            """);
        Assert.AreEqual(2, result.Rank);
        Assert.IsTrue(result.EvidenceHit);
        Assert.IsFalse(result.ScopeIsolated);
    }

    [TestMethod]
    public void RetrievalGate_ProviderErrorIsAFailureRatherThanAnEmptySuccess()
    {
        var result = RetrievalResult.FromJson(Entry, "scope", "Error searching chunks: unavailable");
        Assert.AreEqual(0, result.Rank);
        Assert.IsNotNull(result.Error);
        Assert.IsFalse(result.ScopeIsolated);
    }

    [TestMethod]
    public void PassGate_RequiresAllDocumentsAndQueriesAndRejectsFailedDocuments()
    {
        var pass = new EvalPass("document", 1, "scope");
        Assert.IsFalse(pass.Passed);
        pass.Documents.Add(new("gold.txt", Guid.NewGuid(), "Failed", 10, 0, 0, 0, "failure"));
        pass.Retrieval.Add(new("query", "gold.txt", 1, true, true, "[]", null));
        Assert.AreEqual(1d, pass.RecallAt3);
        Assert.AreEqual(1d, pass.MrrAt3);
        Assert.IsFalse(pass.Passed);
    }

    [TestMethod]
    public void Options_RejectInvalidModesDuplicateFlagsAndInvalidCorpusLimits()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["run", "--mode", "unknown"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["run", "--model", "a", "--model", "b"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["run", "--max-documents", "0"]).Number("max-documents", 3, 1, 100));
        var options = Options.Parse(["compare", "--iterations", "2"]);
        Assert.AreEqual("compare", options.Command);
        Assert.AreEqual(2, options.Number("iterations", 1, 1, 100));
    }
}
