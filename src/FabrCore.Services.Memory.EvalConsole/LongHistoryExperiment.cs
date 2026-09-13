using System.Security.Cryptography;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.EvalConsole;

internal static class LongHistoryExperiment
{
    internal static async Task<int> RunAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.local.json", true).AddEnvironmentVariables().Build();
        var cs = new SqlConnectionStringBuilder(config.GetConnectionString("MemoryEvalDb"));
        if (string.IsNullOrWhiteSpace(cs.InitialCatalog) || new[] { "master", "model", "msdb", "tempdb" }.Contains(cs.InitialCatalog.ToLowerInvariant()))
            throw new InvalidOperationException("Use an existing evaluation database.");
        var models = JsonSerializer.Deserialize<FabrCoreConfiguration>(await File.ReadAllTextAsync(
            values.GetValueOrDefault("models", config["Eval:ModelConfigurationPath"] ?? "fabrcore.json"), ct), json)!;
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        var resolver = new LocalModelResolver(models);
        var clients = new MeasuredClients(new FabrCoreChatClientService(config, logs, resolver));
        var embeddings = new MeasuredEmbeddings(new Embeddings(clients));
        var dimensions = (await embeddings.GetEmbeddings("Memory dimension probe")).Vector.Length;
        embeddings.Drain();
        var model = values.GetValueOrDefault("model", "default");
        var iterations = int.Parse(values.GetValueOrDefault("iterations", "3"));
        if (iterations is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(iterations));
        var run = $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
        var scope = $"memory-long-history:{run}";
        var directory = Path.GetFullPath(Path.Combine(values.GetValueOrDefault("output", "artifacts/memory-long-history"), run));
        Directory.CreateDirectory(directory);
        var facts = new List<Fact>
        {
            new("Atlas current production region", "As of September 7, Atlas production runs in westus3. The eastus deployment was retired.", MemoryType.Fact),
            new("Atlas production region on September 1", "On September 1, Atlas production ran in eastus. This is a historical snapshot.", MemoryType.Fact, true),
            new("Atlas deployment procedure", "To deploy Atlas, validate the migration, take a backup, deploy, then run the health probe.", MemoryType.Procedural),
            new("Mira report format preference", "Mira requires weekly reports as plain text with three bullet points and no attachments.", MemoryType.Rule),
            new("Cedar migration checkpoint", "Resume Cedar migration job RUN-482 at step 18 after obtaining approval from Noor.", MemoryType.Observation)
        };
        for (var i = 0; i < 1195; i++)
            facts.Add(i % 5 == 0
                ? new Fact($"Project P-{i:D4} deployment", $"Project P-{i:D4} production runs in centralus. Its deployment procedure is build, scan, publish. This record concerns only project P-{i:D4}.", MemoryType.Fact)
                : new Fact($"Appliance U-{i:D4} inspection", $"Appliance U-{i:D4} was inspected at depot D-{i:D3} by operator P-{i:D4}. Replace the air filter at its next service.", MemoryType.Observation));
        var questions = new[]
        {
            new Question("current", "What is Atlas's current production region?", [0]),
            new Question("historical", "Where did Atlas production run on September 1?", [1]),
            new Question("procedure", "What steps must I follow to deploy Atlas?", [2]),
            new Question("multi-part", "What is Atlas's current production region, and what steps must I follow to deploy Atlas?", [0, 2]),
            new Question("comparison", "Compare Atlas's production region on September 1 with its current production region.", [0, 1]),
            new Question("preference", "How does Mira want her weekly reports formatted?", [3]),
            new Question("checkpoint", "Where should the Cedar migration resume, and whose approval is needed?", [4]),
            new Question("unknown", "What is Atlas owner's favorite ice cream flavor?", [])
        };
        var cases = new List<object>();
        var status = "running";
        var seedEmbeddings = new List<EmbeddingCallSample>();
        Guid[] recentIds = [];
        var binaryHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(IAgentMemoryService).Assembly.Location, ct)));
        async Task Save() => await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
        {
            Version = "long-history-v1", Status = status, Scope = scope, Model = $"{model}:{(await resolver.GetModelConfigurationAsync(model)).Model}",
            EmbeddingModel = (await resolver.GetModelConfigurationAsync("embeddings")).Model, MemoryBinaryHash = binaryHash,
            MemoryCount = facts.Count, HeaderScanLimit = 200, SemanticCandidateLimit = 20, RecentCandidateLimit = 20,
            CompactIds = true, MinimalSelection = true, Verification = false, RecentIds = recentIds,
            SeedEmbeddings = seedEmbeddings, Cases = cases
        }, json), CancellationToken.None);
        ServiceProvider Services(bool semantic)
        {
            var collection = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<ILoggerFactory>(logs)
                .AddLogging().AddSingleton<IFabrCoreChatClientService>(clients).AddSingleton<IEmbeddings>(embeddings);
            collection.AddAgentMemoryServices("MemoryEvalDb", o =>
            {
                o.EmbeddingDimensions = dimensions; o.Models.RelevanceModelName = model;
                o.Retrieval.RecallGraphHops = 0; o.Retrieval.WarmRetrievalLimit = 5;
                o.Retrieval.UseCompactSelectionIds = true; o.Retrieval.PreferMinimalSelection = true;
                o.Retrieval.UseSemanticCandidates = semantic;
            });
            return collection.BuildServiceProvider();
        }
        try
        {
            await using (var services = Services(false))
            {
                foreach (var hosted in services.GetServices<IHostedService>()) await hosted.StartAsync(ct);
                await services.GetRequiredService<IMemoryScopeService>().EnsureScopeAsync(scope, ct: ct);
                var store = services.GetRequiredService<IMemoryStore>();
                foreach (var batch in facts.Chunk(100))
                {
                    var vectors = await embeddings.GetBatchEmbeddings(batch.Select(f => f.Title + "\n" + f.Content).ToArray());
                    for (var i = 0; i < batch.Length; i++)
                    {
                        var fact = batch[i];
                        var saved = await store.InsertEntityAsync(scope, new MemoryEntry { Title = fact.Title,
                            Description = fact.Content, Type = fact.Type, Temperature = MemoryTemperature.Warm,
                            IsPointInTime = fact.Historical, Metadata = new() { ["source"] = $"synthetic-fixture:{fact.Id}" } }, ct);
                        fact.EntityId = saved.Id;
                        await store.InsertChunkAsync(scope, new MemoryChunkEntry { EntityId = saved.Id,
                            Content = fact.Content, ChunkIndex = 0, Embedding = vectors[i].Vector.ToArray() }, ct);
                    }
                    Console.WriteLine($"Seeded batch of {batch.Length} memories.");
                }
                seedEmbeddings = embeddings.Drain();
                recentIds = (await store.GetHeadersAsync(scope, 200, ct: ct)).Select(h => h.MemoryId).ToArray();
                if (facts.Take(5).Any(f => recentIds.Contains(f.EntityId))) throw new InvalidOperationException("Old targets unexpectedly entered header cap.");
                foreach (var fact in facts)
                    if ((await store.GetPrimaryChunkAsync(scope, fact.EntityId, ct))?.Content != fact.Content)
                        throw new InvalidOperationException("Seeded content failed readback.");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new { facts, questions }, json), ct);
            var allCandidatePassed = true;
            for (var iteration = 0; iteration < iterations; iteration++)
            foreach (var semantic in iteration % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                await using var services = Services(semantic);
                var memory = services.GetRequiredService<IAgentMemoryProvider>().GetMemoryService(scope);
                foreach (var question in questions)
                {
                    clients.Calls.Clear(); embeddings.Drain();
                    var result = await memory.RecallAsync(question.Text, ct: ct);
                    var expected = question.Facts.Select(i => facts[i].EntityId).ToHashSet();
                    var actual = result.WarmMemories.Select(m => m.Id).ToHashSet();
                    var passed = actual.SetEquals(expected) && result.WarmMemories.All(m =>
                        m.Content == facts.Single(f => f.EntityId == m.Id).Content
                        && m.Metadata?["source"] == $"synthetic-fixture:{facts.Single(f => f.EntityId == m.Id).Id}");
                    if (semantic) allCandidatePassed &= passed;
                    cases.Add(new { Iteration = iteration + 1, Semantic = semantic, Question = question.Id,
                        Passed = passed, Expected = expected, Actual = actual, Calls = clients.Calls.ToArray(), Embeddings = embeddings.Drain() });
                    Console.WriteLine($"{iteration + 1} semantic={semantic} {question.Id}: {(passed ? "PASS" : "FAIL")}");
                    await Save();
                }
            }
            status = "complete"; await Save();
            Console.WriteLine(Path.Combine(directory, "report.json"));
            return allCandidatePassed ? 0 : 2;
        }
        catch { status = ct.IsCancellationRequested ? "cancelled" : "failed"; await Save(); throw; }
    }

    private sealed record Fact(string Title, string Content, MemoryType Type, bool Historical = false)
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public Guid EntityId { get; set; }
    }
    private sealed record Question(string Id, string Text, int[] Facts);
}

