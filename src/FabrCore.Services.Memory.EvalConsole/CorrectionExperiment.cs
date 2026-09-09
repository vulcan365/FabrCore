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

internal static class CorrectionExperiment
{
    internal static async Task<int> RunAsync(Dictionary<string, string> values, CancellationToken ct, bool sameType = false, bool sparseHeaders = false, bool delayedBody = false, bool multiChunk = false, bool groupedChunks = false, bool selectiveChunks = false, bool chunkPreview = false, bool lateCorrection = false, bool chunkWindows = false)
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
        var headerMode = delayedBody ? "opaque" : sparseHeaders ? values.GetValueOrDefault("header-mode", "topic") : multiChunk ? values.GetValueOrDefault("header-mode", "rich") : "rich";
        if (!new[] { "rich", "topic", "opaque" }.Contains(headerMode)) throw new ArgumentException("Invalid header-mode.");
        var experiment = lateCorrection ? "late-correction" : chunkPreview ? "chunk-preview" : selectiveChunks ? "selective-chunks" : groupedChunks ? "chunk-coverage" : multiChunk ? "multi-chunk" : delayedBody ? "body-position" : sparseHeaders ? $"headers-{headerMode}" : sameType ? "same-type" : "correction";
        if (chunkWindows) experiment = lateCorrection ? "chunk-windows-late" : "chunk-windows-middle";
        var scope = $"memory-{experiment}:{run}";
        var directory = Path.GetFullPath(Path.Combine(values.GetValueOrDefault("output", $"artifacts/memory-{experiment}"), run));
        Directory.CreateDirectory(directory);
        var facts = new List<Fact>
        {
            new("Atlas current production region", "Atlas production currently runs in eastus.", MemoryType.Fact),
            new("Atlas production region on September 1", "On September 1, Atlas production ran in eastus. This is a historical snapshot.", MemoryType.Fact, true),
            new("Atlas deployment procedure", "To deploy Atlas, validate the migration, take a backup, deploy, then run the health probe.", MemoryType.Procedural),
            new("Mira report format preference", "Mira requires weekly reports as plain text with three bullet points and no attachments.", MemoryType.Rule),
            new("Cedar migration checkpoint", "Resume Cedar migration job RUN-482 at step 18 after obtaining approval from Noor.", MemoryType.Observation)
        };
        for (var i = 0; i < 240; i++)
            facts.Add(i % 2 == 0
                ? new Fact($"Atlas sandbox S-{i:D4} region", $"Atlas sandbox S-{i:D4} runs in centralus. It is an isolated test environment, not production.", MemoryType.Fact)
                : new Fact($"Atlas inspection ticket T-{i:D4}", $"Atlas inspection ticket T-{i:D4} concerns the air filter at depot D-{i:D3}. Operator P-{i:D4} signed off the inspection.", MemoryType.Observation));
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
        var variants = new List<(int Budget, bool Diverse, bool Hybrid, int Preview, bool Matched)> { (0, false, false, 0, false), (20, false, false, 0, false), (8, false, false, 0, false) };
        if (bool.Parse(values.GetValueOrDefault("diverse-candidates", "false"))) variants.Add((8, true, false, 0, false));
        if (sameType)
        {
            facts = new List<Fact>
            {
                new("Atlas production configuration: region", "Atlas production configuration: the hosting region is westus3.", MemoryType.Fact),
                new("Atlas production configuration: owner", "Atlas production configuration: the accountable owner is Noor.", MemoryType.Fact),
                new("Atlas production configuration: retention", "Atlas production configuration: backup retention is 35 days.", MemoryType.Fact),
                new("Atlas production configuration: recovery", "Atlas production configuration: the recovery point objective is 15 minutes.", MemoryType.Fact),
                new("Atlas production configuration: encryption", "Atlas production configuration: encryption at rest uses AES-256.", MemoryType.Fact)
            };
            var types = new[] { MemoryType.Fact, MemoryType.Rule, MemoryType.Procedural, MemoryType.Observation };
            for (var i = 0; i < 240; i++) facts.Add(new Fact($"Sandbox S-{i:D4} inspection record",
                $"Sandbox S-{i:D4} inspection record: the air filter at depot D-{i:D3} requires replacement. This is unrelated to Atlas production configuration.", types[i % types.Length]));
            questions = new[]
            {
                new Question("region", "What is the hosting region in Atlas production configuration?", [0]),
                new Question("owner", "Who is the accountable owner in Atlas production configuration?", [1]),
                new Question("retention", "What is the backup retention in Atlas production configuration?", [2]),
                new Question("recovery", "What is the recovery point objective in Atlas production configuration?", [3]),
                new Question("encryption", "What encryption at rest is used in Atlas production configuration?", [4]),
                new Question("five-facts", "Give all five Atlas production configuration values: hosting region, accountable owner, backup retention, recovery point objective, and encryption at rest.", [0, 1, 2, 3, 4]),
                new Question("unknown", "What is Atlas production configuration's monthly hosting cost?", [])
            };
            variants = [(8, false, false, 0, false), (8, true, false, 0, false), (20, false, false, 0, false), (20, true, false, 0, false)];
        }
        var mutations = new List<object>();
        if (bool.Parse(values.GetValueOrDefault("hybrid-candidates", "false")))
            variants = [(20, false, false, 0, false), (8, false, false, 0, false), (8, true, false, 0, false), (8, false, true, 0, false)];
        if (sparseHeaders) variants = [(8, false, false, 0, false), (8, false, true, 0, false)];
        if (sparseHeaders && values.ContainsKey("selection-preview"))
        {
            var previewLength = int.Parse(values["selection-preview"]);
            if (previewLength is < 1 or > 512) throw new ArgumentOutOfRangeException("selection-preview");
            variants = [(8, false, true, 0, false), (8, false, true, previewLength, false)];
        }
        var status = "running";
        if (selectiveChunks) facts.InsertRange(5, [
            new Fact("Atlas sandbox region", "Atlas sandbox configuration: the hosting region is eastus. This does not describe production.", MemoryType.Fact),
            new Fact("Atlas sandbox owner", "Atlas sandbox configuration: the accountable owner is Inez. This does not describe production.", MemoryType.Fact),
            new Fact("Atlas sandbox retention", "Atlas sandbox configuration: backup retention is 7 days. This does not describe production.", MemoryType.Fact)]);
        var chunkCounts = groupedChunks && values.ContainsKey("chunks-per-memory")
            ? new[] { 1, 3, int.Parse(values["chunks-per-memory"]) }.Distinct().ToArray() : new[] { 1 };
        if (selectiveChunks) chunkCounts = [5, 8];
        var selectionModes = selectiveChunks ? new[] { false, true } : new[] { false };
        var skipModes = new[] { false };
        if (selectiveChunks && bool.Parse(values.GetValueOrDefault("skip-redundant-selection", "false")))
        {
            chunkCounts = [8]; selectionModes = [true]; skipModes = [false, true];
        }
        var seededChunks = new Dictionary<Guid, (Guid EntityId, int Index, string Content)>();
        var coreDescription = string.Join(" ", facts.Take(5).Select(f => f.Content));
        var chunkPreviews = new[] { 0 };
        if (chunkPreview)
        {
            chunkCounts = [8]; selectionModes = [true]; skipModes = [false]; chunkPreviews = [0, 128, 256];
            var padding = string.Concat(Enumerable.Repeat("Administrative commentary records routine review activity. ", 12));
            facts = facts.Select((f, i) => i < 8 ? f with {
                Content = ((i % 2 == 0 ? "" : padding[..144] + "\n") + f.Content + "\n" + padding)[..450]
            } : f).ToList();
        }
        if (lateCorrection)
        {
            chunkCounts = [8]; selectionModes = [true]; skipModes = [false]; chunkPreviews = [0, 256, 384];
            var padding = string.Concat(Enumerable.Repeat("Administrative commentary records routine review activity. ", 12));
            var drafts = new[] {
                "Atlas production configuration: the hosting region is eastus.",
                "Atlas production configuration: the accountable owner is Inez.",
                "Atlas production configuration: backup retention is 7 days." };
            const string correction = "Correction: the preceding value is a rejected draft, not current production. Do not use it as a production fact.";
            facts = facts.Select((f, i) => i < 5 ? f with { Content = (f.Content + "\n" + padding)[..450] }
                : i < 8 ? f with { Content = (drafts[i - 5] + "\n" + padding)[..319] + "\n" + correction + padding[..(450 - 320 - correction.Length)] }
                : f).ToList();
        }
        var tailModes = chunkWindows ? new[] { false, true } : new[] { false };
        if (chunkWindows) chunkPreviews = [0, 288];
        if (chunkCounts.Any(c => c is < 1 or > 8)) throw new ArgumentOutOfRangeException("chunks-per-memory");
        if (groupedChunks)
        {
            variants = [(8, false, true, 0, true), (8, false, true, 256, true)];
            if (chunkCounts.Length > 1) variants = [(8, false, true, 0, true)];
            if (selectiveChunks) variants = [(8, false, true, 0, true)];
            questions = questions.Append(new Question("two-facts", "What are the hosting region and accountable owner in Atlas production configuration?", [0, 1])).ToArray();
        }
        const string overview = "Document overview. Configuration values are recorded in a later section; this opening section contains no configuration values.";
        if (multiChunk && !groupedChunks) variants = [(8, false, true, 0, false), (8, false, true, 256, false)];
        if (multiChunk && !groupedChunks && bool.Parse(values.GetValueOrDefault("matched-chunks", "false")))
            variants = [(8, false, true, 256, false), (8, false, true, 256, true)];
        if (delayedBody)
        {
            var introduction = string.Concat(Enumerable.Repeat("Administrative note: the following section records approved configuration values. ", 3))[..144] + "\n";
            var tail = "\n" + string.Concat(Enumerable.Repeat("Archived operational commentary contains no additional configuration values. ", 12));
            facts = facts.Select(f => f with { Content = introduction + f.Content + tail }).ToList();
            variants = [(8, false, true, 0, false), (8, false, true, 160, false), (8, false, true, 256, false)];
        }
        var seedEmbeddings = new List<EmbeddingCallSample>();
        Guid[] recentIds = [];
        var binaryHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(IAgentMemoryService).Assembly.Location, ct)));
        async Task Save() => await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
        {
            Version = chunkWindows ? "chunk-windows-v1" : lateCorrection ? "late-correction-v1" : chunkPreview ? "chunk-preview-v1" : selectiveChunks ? "selective-chunks-v1" : groupedChunks ? "chunk-coverage-v2" : multiChunk ? "multi-chunk-v2" : delayedBody ? "body-position-v1" : sparseHeaders ? "sparse-headers-v2" : sameType ? "same-type-v2" : "correction-v4", HeaderMode = headerMode, EvidenceChunkIndex = groupedChunks ? (int?)null : multiChunk ? 1 : 0, BodyFactOffset = delayedBody ? 145 : 0, CorrectionOffset = lateCorrection ? (int?)320 : null, Status = status, Scope = scope, Model = $"{model}:{(await resolver.GetModelConfigurationAsync(model)).Model}",
            EmbeddingModel = (await resolver.GetModelConfigurationAsync("embeddings")).Model, MemoryBinaryHash = binaryHash,
            MemoryCount = groupedChunks ? facts.Count - (selectiveChunks ? 7 : 4) : facts.Count, FactCount = facts.Count, HeaderScanLimit = 200, Variants = variants.Select(v => new { v.Budget, v.Diverse, v.Hybrid, v.Preview, v.Matched }), Mutations = mutations,
            ChunkCounts = chunkCounts, SelectionModes = selectionModes, SkipModes = skipModes, ChunkPreviews = chunkPreviews, TailModes = tailModes, FactOffsets = chunkPreview ? new[] { 0, 145, 0, 145, 0, 145, 0, 145 } : null, CompactIds = true, MinimalSelection = true, Verification = false, RecentIds = recentIds,
            SeedEmbeddings = seedEmbeddings, Cases = cases
        }, json), CancellationToken.None);
        ServiceProvider Services(int budget, bool diverse = false, bool hybrid = false, int preview = 0, bool matched = false, int chunkCount = 1, bool selectChunks = false, bool skipRedundant = false, int chunkPreviewLength = 0, bool includeTail = false)
        {
            var collection = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<ILoggerFactory>(logs)
                .AddLogging().AddSingleton<IFabrCoreChatClientService>(clients).AddSingleton<IEmbeddings>(embeddings);
            collection.AddAgentMemoryServices("MemoryEvalDb", o =>
            {
                o.EmbeddingDimensions = dimensions; o.Models.RelevanceModelName = model;
                o.Retrieval.RecallGraphHops = 0; o.Retrieval.WarmRetrievalLimit = 5;
                o.Retrieval.UseCompactSelectionIds = true; o.Retrieval.PreferMinimalSelection = true;
                o.Retrieval.UseSemanticCandidates = budget > 0;
                o.Retrieval.DiversifySemanticCandidates = diverse;
                o.Retrieval.HybridSemanticCandidates = hybrid;
                o.Retrieval.SelectionPreviewCharacters = preview;
                o.Retrieval.UseMatchedChunkEvidence = matched;
                o.Retrieval.MatchedChunksPerMemory = chunkCount;
                o.Retrieval.SelectMatchedChunks = selectChunks;
                o.Retrieval.SkipRedundantChunkSelection = skipRedundant;
                o.Retrieval.ChunkSelectionPreviewCharacters = chunkPreviewLength;
                o.Retrieval.ChunkSelectionIncludeTail = includeTail;
                if (budget > 0) { o.Retrieval.SemanticCandidateLimit = budget; o.Retrieval.RecentCandidateLimit = budget; }
            });
            return collection.BuildServiceProvider();
        }
        try
        {
            await using (var services = Services(0))
            {
                foreach (var hosted in services.GetServices<IHostedService>()) await hosted.StartAsync(ct);
                await services.GetRequiredService<IMemoryScopeService>().EnsureScopeAsync(scope, ct: ct);
                var store = services.GetRequiredService<IMemoryStore>();
                var overviewVector = multiChunk ? await store.GenerateEmbeddingAsync(overview, ct) : null;
                foreach (var batch in facts.Chunk(100))
                {
                    var vectors = await embeddings.GetBatchEmbeddings(batch.Select(f => f.Title + "\n" + f.Content).ToArray());
                    for (var i = 0; i < batch.Length; i++)
                    {
                        var fact = batch[i];
                        fact.StoredTitle = headerMode == "opaque" ? $"Stored memory {fact.Id:N}" : fact.Title;
                        fact.StoredDescription = headerMode == "rich" ? fact.Content
                            : headerMode == "topic" ? $"Details about {fact.Title.ToLowerInvariant()}." : "Details retained in the memory body.";
                        var targetIndex = facts.IndexOf(fact);
                        var groupedTarget = groupedChunks && targetIndex < (selectiveChunks ? 8 : 5);
                        if (groupedTarget)
                        {
                            fact.StoredTitle = "Atlas production configuration";
                            fact.StoredDescription = chunkPreview || lateCorrection ? coreDescription : string.Join(" ", facts.Take(5).Select(f => f.Content));
                        }
                        var saved = groupedTarget && targetIndex > 0 ? new MemoryEntry { Id = facts[0].EntityId }
                            : await store.InsertEntityAsync(scope, new MemoryEntry { Title = fact.StoredTitle,
                            Description = fact.StoredDescription, Type = fact.Type, Temperature = MemoryTemperature.Warm,
                            IsPointInTime = fact.Historical, Metadata = new() { ["source"] = $"synthetic-fixture:{fact.Id}" } }, ct);
                        fact.EntityId = saved.Id;
                        if (multiChunk && !(groupedTarget && targetIndex > 0)) await store.InsertChunkAsync(scope, new MemoryChunkEntry { EntityId = saved.Id,
                            Content = overview, ChunkIndex = 0, Embedding = overviewVector }, ct);
                        fact.EvidenceChunkIndex = groupedTarget ? targetIndex + 1 : multiChunk ? 1 : 0;
                        var evidenceChunk = await store.InsertChunkAsync(scope, new MemoryChunkEntry { EntityId = saved.Id,
                            Content = fact.Content, ChunkIndex = fact.EvidenceChunkIndex, Embedding = vectors[i].Vector.ToArray() }, ct);
                        fact.EvidenceChunkId = evidenceChunk.ChunkId;
                    }
                    Console.WriteLine($"Seeded batch of {batch.Length} memories.");
                }
                seedEmbeddings = embeddings.Drain();
                if (selectiveChunks)
                    foreach (var chunk in await store.GetChunksAsync(scope, facts[0].EntityId, ct))
                        seededChunks[chunk.ChunkId] = (chunk.EntityId, chunk.ChunkIndex, chunk.Content);
                recentIds = (await store.GetHeadersAsync(scope, 200, ct: ct)).Select(h => h.MemoryId).ToArray();
                if (facts.Take(5).Any(f => recentIds.Contains(f.EntityId))) throw new InvalidOperationException("Old targets unexpectedly entered header cap.");
                foreach (var fact in facts)
                    if (multiChunk
                        ? !(await store.GetChunksAsync(scope, fact.EntityId, ct)).Any(c => c.ChunkIndex == fact.EvidenceChunkIndex && c.Content == fact.Content)
                            || (await store.GetPrimaryChunkAsync(scope, fact.EntityId, ct))?.Content != overview
                        : (await store.GetPrimaryChunkAsync(scope, fact.EntityId, ct))?.Content != fact.Content)
                        throw new InvalidOperationException("Seeded content failed readback.");
                if (!sameType)
                {
                // Apply the actual public correction API after the target has aged out of the header cap.
                var memory = services.GetRequiredService<IAgentMemoryProvider>().GetMemoryService(scope);
                var corrected = "As of September 7, Atlas production runs in westus3. The eastus deployment was retired.";
                mutations.Add(new { EntityId = facts[0].EntityId, Before = facts[0].Content, After = corrected });
                await memory.UpdateMemoryAsync(facts[0].EntityId, content: corrected, ct: ct);
                facts[0] = facts[0] with { Content = corrected, StoredDescription = corrected };
                var checkpoint = await memory.UpdateMemoryAsync(facts[4].EntityId,
                    content: "Resume Cedar migration job RUN-482 at step 21 after obtaining approval from Noor.", ct: ct);
                mutations.Add(new { EntityId = facts[4].EntityId, Before = facts[4].Content, After = checkpoint.Content });
                facts[4] = facts[4] with { Content = checkpoint.Content!, StoredDescription = checkpoint.Description };
                foreach (var index in new[] { 0, 4 })
                {
                    var entity = await store.GetEntityByIdAsync(scope, facts[index].EntityId, ct);
                    if (entity?.Description != facts[index].Content ||
                        (await store.GetPrimaryChunkAsync(scope, facts[index].EntityId, ct))?.Content != facts[index].Content)
                        throw new InvalidOperationException("Correction did not update both header and content.");
                }
                seedEmbeddings.AddRange(embeddings.Drain());
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new { facts, questions }, json), ct);
            var allCandidatePassed = true;
            for (var iteration = 0; iteration < iterations; iteration++)
            foreach (var includeTail in tailModes.Skip(iteration % tailModes.Length).Concat(tailModes.Take(iteration % tailModes.Length)))
            foreach (var chunkPreviewLength in chunkPreviews.Skip(iteration % chunkPreviews.Length).Concat(chunkPreviews.Take(iteration % chunkPreviews.Length)))
            foreach (var skipRedundant in skipModes.Skip(iteration % skipModes.Length).Concat(skipModes.Take(iteration % skipModes.Length)))
            foreach (var selectChunks in selectionModes.Skip(iteration % selectionModes.Length).Concat(selectionModes.Take(iteration % selectionModes.Length)))
            foreach (var chunkCount in chunkCounts.Skip(iteration % chunkCounts.Length).Concat(chunkCounts.Take(iteration % chunkCounts.Length)))
            foreach (var (budget, diverse, hybrid, preview, matched) in variants.Skip(iteration % variants.Count).Concat(variants.Take(iteration % variants.Count)))
            {
                if (includeTail && chunkPreviewLength == 0) continue;
                await using var services = Services(budget, diverse, hybrid, preview, matched, chunkCount, selectChunks, skipRedundant, chunkPreviewLength, includeTail);
                var memory = services.GetRequiredService<IAgentMemoryProvider>().GetMemoryService(scope);
                foreach (var question in questions)
                {
                    clients.Calls.Clear(); embeddings.Drain();
                    var result = await memory.RecallAsync(question.Text, ct: ct);
                    var expected = question.Facts.Select(i => facts[i].EntityId).ToHashSet();
                    var actual = result.WarmMemories.Select(m => m.Id).ToHashSet();
                    var idsPassed = actual.SetEquals(expected);
                    var contentPassed = groupedChunks
                        ? question.Facts.All(i => result.WarmMemories.Any(m => m.Id == facts[i].EntityId && m.Content?.Contains(facts[i].Content, StringComparison.Ordinal) == true))
                            && result.WarmMemories.All(m => m.Metadata?["source"] == $"synthetic-fixture:{facts.First(f => f.EntityId == m.Id).Id}")
                        : result.WarmMemories.All(m =>
                        m.Content == facts.Single(f => f.EntityId == m.Id).Content
                        && m.Metadata?["source"] == $"synthetic-fixture:{facts.Single(f => f.EntityId == m.Id).Id}");
                    var provenancePassed = !matched || (groupedChunks
                        ? result.WarmMemories.All(m => m.RecalledChunks is { Count: > 0 } chunks
                            ? chunks.All(c => (selectiveChunks
                                ? seededChunks.TryGetValue(c.ChunkId, out var source) && source.EntityId == m.Id && source.Index == c.ChunkIndex && source.Content == c.Content
                                : facts.Any(f => f.EntityId == m.Id && f.EvidenceChunkId == c.ChunkId
                                && f.EvidenceChunkIndex == c.ChunkIndex && f.Content == c.Content) && !c.IsTruncated)
                                && !c.IsTruncated)
                            : facts.Any(f => f.EntityId == m.Id && f.EvidenceChunkId == m.RecalledChunkId
                                && f.EvidenceChunkIndex == m.RecalledChunkIndex && f.Content == m.Content) && !m.IsContentTruncated)
                        : result.WarmMemories.All(m =>
                        m.RecalledChunkId == facts.Single(f => f.EntityId == m.Id).EvidenceChunkId
                        && m.RecalledChunkIndex == (multiChunk ? 1 : 0) && !m.IsContentTruncated));
                    var expectedChunks = question.Facts.Select(i => facts[i].EvidenceChunkId).ToHashSet();
                    var excessChunks = result.WarmMemories.Sum(m => m.RecalledChunks?.Count(c => !expectedChunks.Contains(c.ChunkId)) ?? 0);
                    var passed = idsPassed && contentPassed && provenancePassed && (!selectiveChunks || excessChunks == 0);
                    var measuredCalls = clients.Calls.ToArray();
                    var measuredEmbeddings = embeddings.Drain();
                    object? diagnostic = null;
                    if (!passed && budget > 0)
                    {
                        // Repeat candidate discovery only for diagnosis; keep its costs separate from recall.
                        var store = services.GetRequiredService<IMemoryStore>();
                        var vector = await store.GenerateEmbeddingAsync(question.Text, ct);
                        var candidateStore = (IMemoryCandidateStore)store;
                        var semantic = hybrid
                            ? await candidateStore.FindHybridCandidateHeadersAsync(scope, vector, budget, ct: ct)
                            : diverse
                            ? await candidateStore.FindDiverseCandidateHeadersAsync(scope, vector, budget, ct: ct)
                            : await candidateStore.FindCandidateHeadersAsync(scope, vector, budget, ct: ct);
                        var recent = await store.GetHeadersAsync(scope, budget, ct: ct);
                        var candidates = semantic.Concat(recent).DistinctBy(h => h.MemoryId).ToArray();
                        diagnostic = new { Candidates = candidates,
                            MissingBeforeSelection = expected.Except(candidates.Select(h => h.MemoryId)).ToArray(),
                            Embeddings = embeddings.Drain() };
                    }
                    if (chunkWindows ? includeTail : lateCorrection ? chunkPreviewLength == 384 : chunkPreview ? chunkPreviewLength == 256 : selectiveChunks ? selectChunks && chunkCount == chunkCounts.Max() : chunkCounts.Length > 1 ? chunkCount == chunkCounts.Max() : variants.Any(v => v.Matched) ? matched : delayedBody ? preview == 256 : variants.Any(v => v.Preview > 0) ? preview > 0 : variants.Any(v => v.Hybrid) ? hybrid : variants.Any(v => v.Diverse) ? diverse : budget > 0) allCandidatePassed &= passed;
                    cases.Add(new { Iteration = iteration + 1, CandidateBudget = budget, Diverse = diverse, Hybrid = hybrid, Preview = preview, Matched = matched, ChunkCount = chunkCount, SelectChunks = selectChunks, SkipRedundant = skipRedundant, ChunkPreview = chunkPreviewLength, IncludeTail = includeTail, ExcessChunks = excessChunks, Question = question.Id,
                        Passed = passed, IdsPassed = idsPassed, ContentPassed = contentPassed, ProvenancePassed = provenancePassed,
                        BodyCharacters = result.WarmMemories.Sum(m => m.Content?.Length ?? 0),
                        ContextCharacters = memory.FormatRecallContext(result).Length,
                        ReturnedChunkCount = result.WarmMemories.Sum(m => m.RecalledChunks?.Count ?? (m.RecalledChunkId.HasValue ? 1 : 0)),
                        LoadedBodies = multiChunk ? result.WarmMemories.Select(m => new { m.Id, m.Content, m.RecalledChunkId, m.RecalledChunkIndex, m.IsContentTruncated, m.RecalledChunks }).ToArray() : null,
                        Expected = expected, Actual = actual, Calls = measuredCalls, Embeddings = measuredEmbeddings,
                        ExpectedChunkIds = question.Facts.Select(i => facts[i].EvidenceChunkId).ToArray(),
                        Diagnostic = diagnostic });
                    Console.WriteLine($"{iteration + 1} chunk-preview={chunkPreviewLength} tail={includeTail} {question.Id}: {(passed ? "PASS" : "FAIL")}");
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
        public Guid EvidenceChunkId { get; set; }
        public int EvidenceChunkIndex { get; set; }
        public string? StoredTitle { get; set; }
        public string? StoredDescription { get; set; }
    }
    private sealed record Question(string Id, string Text, int[] Facts);
}








