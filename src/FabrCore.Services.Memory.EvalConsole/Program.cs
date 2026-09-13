using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.EvalConsole;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Plugin;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using FabrCore.Services.Memory.Services;

return await Run(args);

static async Task<int> Run(string[] args)
{
    var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
    if (args.Contains("--help"))
    {
        Console.WriteLine("Commands: run (default), selection-scale, long-history, corrections, same-type, sparse-headers, body-position, multi-chunk, chunk-coverage, selective-chunks, chunk-preview, late-correction, chunk-windows, compare --baseline FILE --candidate FILE. Run: --mode code|tools|extract|compact|harness --iterations N --compact-ids true|false --minimal-selection true|false --verify-selection true|false --semantic-candidates true|false --diverse-candidates true|false --hybrid-candidates true|false --candidate-budget N --selection-preview N --matched-chunks true|false --chunks-per-memory N --select-chunks true|false --skip-redundant-selection true|false --chunk-selection-preview N --chunk-selection-tail true|false --model NAME --models FILE --manifest FILE --output DIR. Exit: 0 pass, 1 infrastructure, 2 gate failure, 130 cancellation. Uses existing MemoryEvalDb; retains isolated run scopes and reports. selection-scale tests synthetic 200-header selection without SQL or embeddings.");
        return 0;
    }
    var command = args.FirstOrDefault() is { } first && !first.StartsWith("--") ? first : "run";
    var values = new Dictionary<string,string>();
    var runId = $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
    var report = new RunReport { RunId = runId };
    string? reportPath = null;
    using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    try
    {
        for (var i = command == args.FirstOrDefault() ? 1 : 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException("Options require --name value.");
            if (!new[] { "mode", "iterations", "model", "models", "manifest", "output", "baseline", "candidate", "compact-ids", "minimal-selection", "verify-selection", "semantic-candidates", "diverse-candidates", "candidate-budget", "hybrid-candidates", "header-mode", "selection-preview", "matched-chunks", "chunks-per-memory", "select-chunks", "skip-redundant-selection", "chunk-selection-preview", "chunk-selection-tail", "window-fixture" }.Contains(args[i][2..])) throw new ArgumentException($"Unknown option {args[i]}");
            values.Add(args[i][2..], args[i+1]);
        }
        string Value(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        if (command == "compare")
        {
            var a = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(Value("baseline", "")), json)!;
            var b = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(Value("candidate", "")), json)!;
            var regressions = ReportComparison.Regressions(a, b);
            Console.WriteLine($"{a.RunId} ({a.Mode}/{a.Model}) -> {b.RunId} ({b.Mode}/{b.Model})");
            Console.WriteLine($"Checks: {a.Checks.Count(c => c.Passed)}/{a.Checks.Count} -> {b.Checks.Count(c => c.Passed)}/{b.Checks.Count}; chat calls: {a.Calls.Count} -> {b.Calls.Count}");

            foreach (var c in regressions) Console.WriteLine($"REGRESSION {c}");
            return regressions.Count == 0 && b.Checks.All(c => c.Passed) ? 0 : 2;
        }
        if (command == "selection-scale") return await SelectionScaleExperiment.RunAsync(values, cancel.Token);
        if (command == "long-history") return await LongHistoryExperiment.RunAsync(values, cancel.Token);
        if (command == "corrections") return await CorrectionExperiment.RunAsync(values, cancel.Token);
        if (command == "same-type") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true);
        if (command == "sparse-headers") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, sparseHeaders: true);
        if (command == "body-position") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, sparseHeaders: true, delayedBody: true);
        if (command == "multi-chunk") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, multiChunk: true);
        if (command == "chunk-windows")
        {
            var fixture = values.GetValueOrDefault("window-fixture", "late");
            if (fixture is not ("late" or "middle")) throw new ArgumentException("window-fixture must be late or middle.");
            return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, multiChunk: true, groupedChunks: true, selectiveChunks: true, chunkPreview: fixture == "middle", lateCorrection: fixture == "late", chunkWindows: true);
        }
        if (command == "late-correction") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, multiChunk: true, groupedChunks: true, selectiveChunks: true, lateCorrection: true);
        if (command == "chunk-preview") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, multiChunk: true, groupedChunks: true, selectiveChunks: true, chunkPreview: true);
        if (command == "selective-chunks") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, multiChunk: true, groupedChunks: true, selectiveChunks: true);
        if (command == "chunk-coverage") return await CorrectionExperiment.RunAsync(values, cancel.Token, sameType: true, multiChunk: true, groupedChunks: true);
        if (command != "run") throw new ArgumentException("Unknown command.");
        report.Mode = Value("mode", "code");
        if (!new[] { "code", "tools", "extract", "compact", "harness" }.Contains(report.Mode)) throw new ArgumentException("Invalid mode.");
        report.Iterations = int.Parse(Value("iterations", "1"));
        if (report.Iterations is < 1 or > 100) throw new ArgumentException("Iterations must be 1..100.");
        var directory = Path.Combine(Path.GetFullPath(Value("output", "artifacts/memory-evals")), runId);
        Directory.CreateDirectory(directory);
        reportPath = Path.Combine(directory, "report.json");
        var corpusBytes = await File.ReadAllBytesAsync(Value("manifest", Path.Combine(AppContext.BaseDirectory, "corpus.json")), cancel.Token);
        report.CorpusHash = Convert.ToHexString(SHA256.HashData(corpusBytes));
        await File.WriteAllBytesAsync(Path.Combine(directory, "corpus.json"), corpusBytes, cancel.Token);
        report.MemoryBinaryHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(IAgentMemoryService).Assembly.Location, cancel.Token)));
        report.SdkBinaryHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(FabrCoreHarnessAgent).Assembly.Location, cancel.Token)));
        var corpus = JsonSerializer.Deserialize<Corpus>(corpusBytes, json) ?? throw new ArgumentException("Invalid corpus.");
        if (corpus.Memories.Count == 0 || corpus.Questions.Count == 0 || corpus.Questions.Select(q => q.Id).Distinct().Count() != corpus.Questions.Count)
            throw new ArgumentException("Corpus must contain memories and uniquely identified questions.");
        report.CorpusVersion = corpus.Version;
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.local.json", true).AddEnvironmentVariables().Build();
        var cs = config.GetConnectionString("MemoryEvalDb") ?? throw new InvalidOperationException("Configure ConnectionStrings:MemoryEvalDb.");
        var sql = new SqlConnectionStringBuilder(cs);
        if (string.IsNullOrWhiteSpace(sql.InitialCatalog) || new[] { "master", "model", "msdb", "tempdb" }.Contains(sql.InitialCatalog.ToLowerInvariant())) throw new InvalidOperationException("Use an existing evaluation database.");
        report.Database = $"{sql.DataSource}/{sql.InitialCatalog}";
        var modelPath = Value("models", config["Eval:ModelConfigurationPath"] ?? "fabrcore.json");
        var models = JsonSerializer.Deserialize<FabrCoreConfiguration>(await File.ReadAllTextAsync(modelPath, cancel.Token), json)!;
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        var resolver = new LocalModelResolver(models);
        var clients = new MeasuredClients(new FabrCoreChatClientService(config, logs, resolver));
        var model = Value("model", "default");
        var selected = await resolver.GetModelConfigurationAsync(model);
        report.Model = $"{model}:{selected.Provider}:{selected.Model}";
        report.EmbeddingModel = (await resolver.GetModelConfigurationAsync("embeddings")).Model;
        var embeddings = new MeasuredEmbeddings(new Embeddings(clients));
        var dimensions = (await embeddings.GetEmbeddings("Memory evaluation vector probe")).Vector.Length;
        report.EmbeddingDimensions = dimensions;
        embeddings.Drain(); // Separate provider/dimension warmup from benchmark work.
        try
        {
            for (var iteration = 1; iteration <= report.Iterations; iteration++)
            {
                var scope = $"memory-eval:{runId}:{iteration}";
                report.Scopes.Add(scope);
                var collection = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<ILoggerFactory>(logs)
                    .AddLogging().AddSingleton<IFabrCoreChatClientService>(clients).AddSingleton<IEmbeddings>(embeddings);
                collection.AddAgentMemoryServices("MemoryEvalDb", o => {
                    o.EmbeddingDimensions = dimensions;
                    o.Models.RelevanceModelName = model; o.Models.CompactionModelName = model;
                    o.Retrieval.WarmRetrievalLimit = 5; o.Retrieval.RecallGraphHops = 0;
                    o.Retrieval.UseCompactSelectionIds = bool.Parse(Value("compact-ids", "false"));
                    o.Retrieval.PreferMinimalSelection = bool.Parse(Value("minimal-selection", "false"));
                    o.Retrieval.VerifyMultiMemorySelection = bool.Parse(Value("verify-selection", "false"));
                    o.Retrieval.UseSemanticCandidates = bool.Parse(Value("semantic-candidates", "false"));
                    o.Retrieval.DiversifySemanticCandidates = bool.Parse(Value("diverse-candidates", "false"));
                    o.Retrieval.HybridSemanticCandidates = bool.Parse(Value("hybrid-candidates", "false"));
                    o.Retrieval.SelectionPreviewCharacters = int.Parse(Value("selection-preview", "0"));
                    o.Retrieval.UseMatchedChunkEvidence = bool.Parse(Value("matched-chunks", "false"));
                    o.Retrieval.MatchedChunksPerMemory = int.Parse(Value("chunks-per-memory", "1"));
                    o.Retrieval.SelectMatchedChunks = bool.Parse(Value("select-chunks", "false"));
                    o.Retrieval.SkipRedundantChunkSelection = bool.Parse(Value("skip-redundant-selection", "false"));
                    o.Retrieval.ChunkSelectionPreviewCharacters = int.Parse(Value("chunk-selection-preview", "0"));
                    o.Retrieval.ChunkSelectionIncludeTail = bool.Parse(Value("chunk-selection-tail", "false"));
                    if (values.ContainsKey("candidate-budget"))
                    {
                        var budget = int.Parse(values["candidate-budget"]);
                        if (budget is < 1 or > 100) throw new ArgumentOutOfRangeException("candidate-budget");
                        o.Retrieval.SemanticCandidateLimit = budget;
                        o.Retrieval.RecentCandidateLimit = budget;
                    }
                    o.Consolidation.EntityMatchThreshold = 0.03; o.Consolidation.EnableRelationshipExtraction = false;
                });
                await using var services = collection.BuildServiceProvider();
                report.Options = JsonSerializer.SerializeToElement(services.GetRequiredService<AgentMemoryOptions>(), json);
                foreach (var hosted in services.GetServices<IHostedService>()) await hosted.StartAsync(cancel.Token);
                var provider = services.GetRequiredService<IAgentMemoryProvider>();
                var memory = provider.GetMemoryService(scope);
                var sw = Stopwatch.StartNew();
                var plugin = new AgentMemoryPlugin(memory);
                async Task<string> Tool(string name, Dictionary<string,object?> arguments)
                {
                    var function = AIFunctionFactory.Create(typeof(AgentMemoryPlugin).GetMethod(name)!, plugin);
                    return (await function.InvokeAsync(new AIFunctionArguments(arguments), cancel.Token))?.ToString() ?? "";
                }
                if (report.Mode == "compact")
                {
                    var trajectory = new[] { "Current task checkpoint: restart job RUN-482 at step 18; final approval is pending." }
                        .Concat(corpus.Memories.Select(m => m.Content))
                        .Concat(Enumerable.Repeat(string.Concat(Enumerable.Repeat("Routine progress check: processing continues. No new result, decision, approval or error was reported. ", 4)), 30))
                        .Concat([
                        "The latest progress check reported no new result.",
                        "Please continue only after checking the pending approval."
                    ]).ToList();
                    await File.WriteAllTextAsync(Path.Combine(directory, $"trajectory-{iteration}.json"), JsonSerializer.Serialize(trajectory, json), cancel.Token);
                    var (host, state) = EvalHistoryHost.From(trajectory);
                    var history = new FabrCoreChatHistoryProvider(host, "eval");
                    var result = await services.GetRequiredService<MemoryAwareCompactionService>().CompactAsync(history,
                        new CompactionConfig { Enabled = true, MaxContextTokens = 4000, Threshold = 0.5, KeepLastN = 2 },
                        memory, services.GetRequiredService<AgentMemoryOptions>(), model, cancel.Token);
                    report.Checks.Add(new($"{iteration}:compaction", "compaction", result.WasCompacted && result.EstimatedTokensAfter < result.EstimatedTokensBefore
                        && state.Messages.Any(m => m.AuthorName == "compaction" && m.ContentsJson?.Contains("RUN-482") == true), sw.ElapsedMilliseconds, JsonSerializer.Serialize(new { result, messages = state.Messages }, json)));
                }
                foreach (var m in report.Mode == "compact" ? [] : corpus.Memories)
                {
                    if (report.Mode == "extract")
                        await memory.ExtractMemoriesAsync([new ChatMessage(ChatRole.User, m.Content)], cancel.Token);
                    else if (report.Mode == "tools")
                    {
                        var result = await Tool(nameof(AgentMemoryPlugin.SaveMemory), new() { ["title"] = m.Title, ["type"] = m.Type, ["content"] = m.Content, ["isPointInTime"] = m.Snapshot });
                        using var parsed = JsonDocument.Parse(result);
                        if (!parsed.RootElement.TryGetProperty("memoryId", out _)) throw new InvalidOperationException("Save tool failed.");
                    }
                    else await memory.SaveMemoryAsync(m.Title, Enum.Parse<MemoryType>(m.Type), m.Content, isPointInTime: m.Snapshot, ct: cancel.Token);
                    report.Calls = clients.Calls.ToList();
                    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, json), cancel.Token);
                }
                report.Checks.Add(new($"{iteration}:ingestion", "persistence", (await memory.GetMemoryIndexAsync(cancel.Token)).Entries.Count > 0, sw.ElapsedMilliseconds, ""));
                // Rebuild the full DI graph, not just the facade, to check persisted recall.
                await using var restarted = collection.BuildServiceProvider();
                memory = restarted.GetRequiredService<IAgentMemoryProvider>().GetMemoryService(scope);
                plugin = new AgentMemoryPlugin(memory);
                foreach (var q in corpus.Questions)
                {
                    sw.Restart();
                    string content;
                    int count;
                    if (report.Mode == "harness")
                    {
                        var harnessOptions = new FabrCoreHarnessOptions {
                            DisableOpenTelemetry = true,
                            ChatOptions = new ChatOptions { Instructions = "Answer using retrieved memory only. If the requested fact is absent, respond with exactly UNKNOWN. Preserve identifiers and relevant dates. Distinguish pending approval from granted approval." }
                        };
                        harnessOptions.WithMemory(memory);
                        var harness = new FabrCoreHarnessAgent(await clients.GetChatClient(model), harnessOptions);
                        content = (await harness.RunAsync(q.Query, await harness.CreateSessionAsync(), cancellationToken: cancel.Token)).Text;
                        count = content.Trim().Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    }
                    else if (report.Mode == "tools")
                    {
                        using var parsed = JsonDocument.Parse(await Tool(nameof(AgentMemoryPlugin.RecallMemories), new() { ["query"] = q.Query }));
                        var warm = parsed.RootElement.GetProperty("warmMemories");
                        content = string.Join("\n", warm.EnumerateArray().Select(m => m.GetProperty("content").GetString()));
                        count = warm.GetArrayLength();
                    }
                    else
                    {
                        var recall = await memory.RecallAsync(q.Query, ct: cancel.Token);
                        content = string.Join("\n", recall.WarmMemories.Select(m => m.Content));
                        count = recall.WarmMemories.Count;
                    }
                    var passed = q.Empty ? count == 0 : q.Required.All(t => content.Contains(t, StringComparison.OrdinalIgnoreCase)) && q.Forbidden.All(t => !content.Contains(t, StringComparison.OrdinalIgnoreCase));
                    report.Checks.Add(new($"{iteration}:{q.Id}", q.Category, passed, sw.ElapsedMilliseconds, content));
                    report.Calls = clients.Calls.ToList();
                    Console.WriteLine($"{q.Id}: {(passed ? "PASS" : "FAIL")} ({sw.ElapsedMilliseconds}ms)");
                    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, json), cancel.Token);
                }
                var coreFact = await memory.SaveMemoryAsync("Core canary", MemoryType.Fact, "CORE-CANARY-5831", metadata: new() { ["eval"] = "canary" }, ct: cancel.Token);
                var own = provider.ForInternalAgent(scope, "research", InternalAgentMemoryMode.OwnOnly);
                var layered = provider.ForInternalAgent(scope, "research", InternalAgentMemoryMode.CoreAndOwn);
                var ownFact = await layered.SaveMemoryAsync("Private canary", MemoryType.Fact, "PRIVATE-CANARY-8729", metadata: new() { ["eval"] = "canary" }, ct: cancel.Token);
                var store = services.GetRequiredService<IMemoryStore>();
                async Task Check(string id, bool passed) { report.Checks.Add(new($"{iteration}:{id}", "lifecycle", passed, 0, "")); await Task.CompletedTask; }
                await Check("own-isolation", await store.GetEntityByIdAsync(own.ScopeKey, coreFact.Id, cancel.Token) is null);
                await Check("core-isolation", await store.GetEntityByIdAsync(scope, ownFact.Id, cancel.Token) is null);
                await Check("shared-core", provider.ForInternalAgent(scope, "research", InternalAgentMemoryMode.CoreOnly).ScopeKey == scope);
                await Check("layered-index", (await layered.GetMemoryIndexAsync(cancel.Token)).Entries.Any(e => e.MemoryId == coreFact.Id) && (await layered.GetMemoryIndexAsync(cancel.Token)).Entries.Any(e => e.MemoryId == ownFact.Id));
                await Check("cannot-forget-core", !await layered.ForgetMemoryAsync(coreFact.Id, cancel.Token) && await store.GetEntityByIdAsync(scope, coreFact.Id, cancel.Token) is not null);
                await memory.UpdateMemoryAsync(coreFact.Id, content: "CORRECTED-CANARY-9914", ct: cancel.Token);
                await Check("correction", (await store.GetPrimaryChunkAsync(scope, coreFact.Id, cancel.Token))?.Content == "CORRECTED-CANARY-9914");
                await memory.UpdateMemoryAsync(coreFact.Id, temperature: MemoryTemperature.Cold, ct: cancel.Token);
                await Check("archive", !(await memory.GetMemoryIndexAsync(cancel.Token)).Entries.Any(e => e.MemoryId == coreFact.Id) && await store.GetEntityByIdAsync(scope, coreFact.Id, cancel.Token) is not null);
                await memory.UpdateMemoryAsync(coreFact.Id, temperature: MemoryTemperature.Warm, ct: cancel.Token);
                await Check("restore", (await memory.GetMemoryIndexAsync(cancel.Token)).Entries.Any(e => e.MemoryId == coreFact.Id));
                await memory.ForgetMemoryAsync(coreFact.Id, cancel.Token);
                await Check("forget", await store.GetPrimaryChunkAsync(scope, coreFact.Id, cancel.Token) is null && await store.GetEntityByIdAsync(scope, coreFact.Id, cancel.Token) is null);
                report.Scopes.Add(own.ScopeKey);
            }
            report.Status = "complete";
            report.CompletedAt = DateTimeOffset.UtcNow;
        }
        finally { report.Calls = clients.Calls.ToList(); report.EmbeddingCalls = embeddings.Drain(); }
        return report.Checks.All(c => c.Passed) ? 0 : 2;
    }
    catch (OperationCanceledException) { report.Status = "canceled"; return 130; }
    catch (Exception ex) { report.Status = "error"; report.Error = ex.GetType().Name; Console.Error.WriteLine($"Evaluation failed ({ex.GetType().Name}): {ex.Message}"); return 1; }
    finally
    {
        if (reportPath is not null)
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, json));
            Console.WriteLine($"{report.Status}: {report.Checks.Count(c => c.Passed)}/{report.Checks.Count} checks; {report.Calls.Count} chat calls. Report: {reportPath}");
        }
    }
}

internal sealed record Corpus(string Version, List<MemoryInput> Memories, List<Question> Questions);
internal sealed record MemoryInput(string Title, string Type, string Content, bool Snapshot = false);
internal sealed record Question(string Id, string Category, string Query, string[] Required, string[] Forbidden, bool Empty = false);
internal sealed record CheckResult(string Id, string Category, bool Passed, long Ms, string Retrieved);
internal sealed class RunReport
{
    public string BenchmarkVersion { get; set; } = "2";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string Mode { get; set; } = "";
    public string Model { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimensions { get; set; }
    public int Iterations { get; set; }
    public string CorpusHash { get; set; } = "";
    public string CorpusVersion { get; set; } = "";
    public string Database { get; set; } = "";
    public string MemoryBinaryHash { get; set; } = "";
    public string SdkBinaryHash { get; set; } = "";
    public JsonElement? Options { get; set; }
    public List<string> Scopes { get; set; } = [];
    public List<CheckResult> Checks { get; set; } = [];
    public List<Call> Calls { get; set; } = [];
    public List<EmbeddingCallSample> EmbeddingCalls { get; set; } = [];
}















