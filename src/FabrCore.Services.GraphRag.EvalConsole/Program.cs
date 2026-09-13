using System.Diagnostics;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Services.GraphRag;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.EvalConsole;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine("""
            FabrCore.Services.GraphRag.EvalConsole
            Commands: init | fetch | run (default) | compare | trace
            Options:
              --mode document|legacy|vector   Pipeline for run (default document)
              --taxonomy-cache off|on        Cache combined/classifier responses with exact context (requires result-cache)
              --embedding-cache off|on       Exact scoped embedding reuse (default off)
              --result-cache off|on          Validated graph-batch cache (default off)
              --reingest fresh|repeat|edit    Same scope + forced rebuild; edit changes final character from pass 3
              --repair off|once              One bounded endpoint repair per document (requires spans)
              --evidence off|strict|spans           Source-evidence validation experiment (requires schema)
              --relations current|defined|policy|policy-obligation    Relationship convention experiment
              --response prompt|schema|json|json-guided  JSON object modes; guided adds system shape guidance and temperature 0
                         json-normalized    Guided JSON plus array flattening; retains original responses
              --description-chars N           Schema description target, 0 = unchanged
              --model NAME                   Extraction model alias (default default)
              --models PATH                  FabrCore JSON model/key configuration
              --manifest PATH                Corpus manifest (default bundled corpus.json)
              --max-documents N              Default 3
              --max-chars N                  Source prefix length, default 30000; 0 = full text
              --endpoint-aliases off|on      Resolve unique explicit initialisms (default off)
              --input PATH                   Existing report or comparison JSON for trace
              --sections-per-batch N         Document graph extraction section limit (1-256, default configured/8)
              --taxonomy-names current|structured   Separate taxonomy names from metadata (default current)
              --document-concurrency N       Concurrent documents sharing service limits (1-16, default 1; caches off)
              --iterations N                 Default 1; compare alternates pipeline order
              --output PATH                  Default artifacts/graphrag-evals
              --cache PATH                   Default artifacts/graphrag-corpus
            Reads appsettings.json, appsettings.local.json and environment variables.
            Fresh passes use isolated scopes; repeat/edit reuse the run scope and force rebuilding.
            No database creation, database deletion, or global cleanup is performed.
            Exit codes: 0 passed, 1 infrastructure/configuration error, 2 evaluation gate failed, 130 canceled.
            """);
        return 0;
    }
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    try
    {
        var options = Options.Parse(args);
        if (options.Command == "trace") return await TraceReport.RunAsync(options, cancel.Token);
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables().Build();
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var output = Path.GetFullPath(options.Value("output", "artifacts/graphrag-evals"));
        var cache = Path.GetFullPath(options.Value("cache", "artifacts/graphrag-corpus"));
        var manifest = Path.GetFullPath(options.Value("manifest", Path.Combine(AppContext.BaseDirectory, "corpus.json")));
        List<CorpusDocument> corpus = [];
        if (options.Command != "init")
        {
            corpus = await Corpus.LoadAsync(manifest, cache, options.Number("max-documents", 3, 1, 100),
                options.Number("max-chars", 30_000, 0, 5_000_000), cancel.Token);
            if (options.Command == "fetch") return 0;
        }
        var connectionString = config.GetConnectionString("GraphRagTestDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings:GraphRagTestDb in appsettings.local.json.");
        var sqlInfo = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(sqlInfo.InitialCatalog) || new[] { "master", "model", "msdb", "tempdb" }.Contains(sqlInfo.InitialCatalog.ToLowerInvariant()))
            throw new InvalidOperationException("Specify an existing, dedicated evaluation database.");
        Console.WriteLine($"Database: {sqlInfo.DataSource}/{sqlInfo.InitialCatalog}");
        await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, logs.CreateLogger("Schema"));
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancel.Token);
            await using var probe = new SqlCommand("SELECT VECTOR_DISTANCE('cosine', CAST('[1,0]' AS VECTOR(2)), CAST('[1,0]' AS VECTOR(2)))", connection);
            await probe.ExecuteScalarAsync(cancel.Token);
        }
        Console.WriteLine("Schema migrations and SQL VECTOR probe passed.");
        if (options.Command == "init") return 0;

        var modelPath = options.Value("models", config["Eval:ModelConfigurationPath"] ?? "fabrcore.json");
        var models = JsonSerializer.Deserialize<FabrCoreConfiguration>(await File.ReadAllTextAsync(modelPath, cancel.Token),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Invalid model configuration.");
        var resolver = new LocalModelResolver(models);
        var clients = new MeasuredChatClientService(new FabrCoreChatClientService(config, logs, resolver),
            captureResponses: true, useJsonObjectResponses: options.Value("response", "prompt") is "json" or "json-guided" or "json-normalized",
            guideJsonObjectResponses: options.Value("response", "prompt") is "json-guided" or "json-normalized",
            normalizeJsonArrays: options.Value("response", "prompt") == "json-normalized");
        var embeddings = new MeasuredEmbeddings(new Embeddings(clients));
        var warmup = await embeddings.GetEmbeddings("GraphRAG evaluation embedding dimension probe");
        if (warmup.Vector.Length != 1536) throw new InvalidOperationException($"GraphRAG requires 1536 dimensions; provider returned {warmup.Vector.Length}.");
        var modelName = options.Value("model", config["Eval:ExtractionModel"] ?? "default");
        var selectedModel = options.Command != "compare" && options.Value("mode", "document") == "vector"
            ? null : await resolver.GetModelConfigurationAsync(modelName, cancel.Token);
        var embeddingModel = await resolver.GetModelConfigurationAsync("embeddings", cancel.Token);
        var embeddingCache = new EmbeddingResultCache();
        var resultCache = new ExtractionResultCache();
        await using var services = new ServiceCollection().AddSingleton(resultCache).AddSingleton<IFabrCoreChatClientService>(clients).BuildServiceProvider();
        var audit = new GraphRagAuditLog(config, logs.CreateLogger<GraphRagAuditLog>(), "GraphRagTestDb");
        var scopes = new KnowledgeScopeService(config, logs.CreateLogger<KnowledgeScopeService>(), "GraphRagTestDb", audit);
        var search = new KnowledgeSearchService(config, logs.CreateLogger<KnowledgeSearchService>(), "GraphRagTestDb", audit, embeddings);
        var runId = $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
        var directory = Path.Combine(output, runId);
        Directory.CreateDirectory(directory);
        var report = new EvalReport(runId, DateTimeOffset.UtcNow, sqlInfo.DataSource, sqlInfo.InitialCatalog,
            selectedModel?.Name ?? "not-used", selectedModel?.Provider ?? "not-used", selectedModel?.Model ?? "not-used", embeddingModel.Model,
            corpus.Select(d => new SourceInfo(d.Entry.FileName, d.Entry.Url, d.Sha256, d.SourceSha256, d.Content.Length, d.SourceCharacters)).ToList());
        Console.WriteLine($"Report directory: {directory}");
        report.TaxonomyNames = options.Value("taxonomy-names", "current");
        report.DocumentConcurrency = options.Number("document-concurrency", 1, 1, 16);
        report.EndpointAliases = options.Value("endpoint-aliases", "off");
        report.SectionsPerBatch = options.Number("sections-per-batch", Math.Clamp(config.GetValue("GraphRag:Ingestion:MaxSectionsPerExtractionBatch", 8), 1, 256), 1, 256);
        report.TaxonomyCache = options.Value("taxonomy-cache", "off");
        report.EmbeddingCache = options.Value("embedding-cache", "off");
        report.ResultCache = options.Value("result-cache", "off");
        report.Reingest = options.Value("reingest", "fresh");
        report.RepairMode = options.Value("repair", "off");
        report.EvidenceMode = options.Value("evidence", "off");
        report.RelationConventions = options.Value("relations", "current");
        report.ResponseFormat = options.Value("response", "prompt");
        report.DescriptionTargetChars = options.Number("description-chars", 0, 0, 2000);
        if (report.DescriptionTargetChars > 0 && report.ResponseFormat != "schema")
            throw new ArgumentException("--description-chars requires --response schema.");
        if (report.EvidenceMode != "off" && (report.ResponseFormat != "schema" || options.Value("mode", "document") != "document" || options.Command == "compare"))
            throw new ArgumentException("--evidence strict/spans requires --response schema and run --mode document.");
        if (report.RepairMode == "once" && report.EvidenceMode != "spans")
            throw new ArgumentException("--repair once requires --evidence spans.");
        var iterations = options.Number("iterations", 1, 1, 100);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var modes = options.Command == "compare"
                ? (iteration % 2 == 0 ? new[] { "legacy", "document" } : new[] { "document", "legacy" })
                : new[] { options.Value("mode", "document") };
            foreach (var mode in modes)
            {
                var scope = $"eval:graphrag:{runId}:{(report.Reingest == "fresh" ? iteration + 1 : 1)}:{mode}";
                if (iteration == 0 || report.Reingest == "fresh") await scopes.CreateScopeAsync(scope, $"Public RFC corpus evaluation: {mode}", ct: cancel.Token);
                var pass = new EvalPass(mode, iteration + 1, scope);
                report.Passes.Add(pass);
                var passConfig = new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GraphRag:Ingestion:UseStructuredTaxonomyNames"] = (report.TaxonomyNames == "structured").ToString(),
                    ["GraphRag:Ingestion:ResolveExtractionEndpointAliases"] = (report.EndpointAliases == "on").ToString(),
                    ["GraphRag:Ingestion:MaxSectionsPerExtractionBatch"] = report.SectionsPerBatch.ToString(),
                    ["GraphRag:Ingestion:CacheTaxonomyResponses"] = (report.TaxonomyCache == "on").ToString(),
                    ["GraphRag:Ingestion:UseExtractionResultCache"] = (report.ResultCache == "on").ToString(),
                    ["GraphRag:Ingestion:UseExtractionSourceSpans"] = (report.EvidenceMode == "spans").ToString(),
                    ["GraphRag:Ingestion:RepairExtractionEndpoints"] = (report.RepairMode == "once").ToString(),
                    ["GraphRag:Ingestion:UseExtractionEvidence"] = (report.EvidenceMode == "strict").ToString(),
                    ["GraphRag:Ingestion:UsePolicyRelations"] = (report.RelationConventions is "policy" or "policy-obligation").ToString(),
                    ["GraphRag:Ingestion:UsePolicyObligations"] = (report.RelationConventions == "policy-obligation").ToString(),
                    ["GraphRag:Ingestion:UseExtractionRelationGuidance"] = (report.RelationConventions == "defined").ToString(),
                    ["GraphRag:Ingestion:UseExtractionJsonSchema"] = (report.ResponseFormat == "schema").ToString(),
                    ["GraphRag:Ingestion:ExtractionDescriptionTargetChars"] = report.DescriptionTargetChars.ToString(),
                    ["GraphRag:Ingestion:UseDocumentExtractionPlan"] = (mode != "legacy").ToString(),
                    ["GraphRag:Ingestion:EnableExtraction"] = (mode != "vector").ToString()
                }).Build();
                IEmbeddings ingestionEmbeddings = report.EmbeddingCache == "on"
                    ? new CachedEmbeddings(embeddings, embeddingCache,
                        ExtractionResultCache.Key(connectionString, scope), JsonSerializer.Serialize(embeddingModel), 1536)
                    : embeddings;
                var ingestion = new KnowledgeIngestionService(passConfig, logs.CreateLogger<KnowledgeIngestionService>(),
                    "GraphRagTestDb", audit, ingestionEmbeddings, serviceProvider: services, extractionModelName: modelName);
                pass.TaxonomyRowsAtStart = await EvalDatabase.TaxonomyCountAsync(connectionString, cancel.Token);
                await ReportWriter.WriteAsync(directory, report);
                clients.ResetPeakConcurrency();
                var batchTimer = Stopwatch.StartNew();
                var results = new (CorpusDocument Doc, DocumentResult Item)[corpus.Count];
                try
                {
                    await Parallel.ForEachAsync(Enumerable.Range(0, corpus.Count),
                        new ParallelOptions { MaxDegreeOfParallelism = report.DocumentConcurrency, CancellationToken = cancel.Token },
                        async (index, token) =>
                    {
                        using var measurements = new DocumentMeasurements();
                        var originalDoc = corpus[index];
                        // A one-character tail mutation keeps section offsets/batch count stable.
                        var content = report.Reingest == "edit" && iteration >= 2
                            ? originalDoc.Content[..^1] + (originalDoc.Content[^1] == ' ' ? "\n" : " ") : originalDoc.Content;
                        var doc = originalDoc with { Content = content };
                        var hitsBefore = resultCache.Hits;
                        var embeddingHitsBefore = embeddingCache.Hits;

                        Console.WriteLine($"[{mode}/{iteration + 1}] Ingesting {doc.Entry.FileName}...");
                        var sw = Stopwatch.StartNew();
                        var instructions = doc.Entry.ExtractionInstructions ??
                            "Extract named standards, formats, organizations, technologies, and their explicit technical relationships. Avoid header/footer and copyright boilerplate entities.";
                        var dto = await ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(doc.Entry.FileName, scope, doc.Content,
                            instructions) { ForceReingestion = report.Reingest != "fresh" }, token);
                        var item = new DocumentResult(doc.Entry.FileName, dto.DocumentId, dto.Status, sw.ElapsedMilliseconds,
                            dto.ChunkCount, dto.ExtractedEntityCount, dto.ExtractedRelationshipCount, dto.ErrorMessage);
                        item.ExtractionInstructions = instructions;
                        item.EmbeddingCacheHits = embeddingCache.Hits - embeddingHitsBefore;
                        item.EmbeddingCalls = measurements.EmbeddingCalls.ToList();
                        item.ExtractionCacheHits = resultCache.Hits - hitsBefore;
                        item.ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(doc.Content)));
                        item.ChatCalls = measurements.ChatCalls.ToList();
                        results[index] = (doc, item);
                        Console.WriteLine($"  {item.Status}: {item.WallMs / 1000d:F1}s, {item.Chunks} chunks, {item.Entities} entities");
                    });
                }
                finally
                {
                    pass.IngestionWallMs = batchTimer.ElapsedMilliseconds;
                    pass.PeakChatConcurrency = clients.PeakConcurrency;
                    pass.Documents.AddRange(results.Where(r => r.Item is not null).Select(r => r.Item));
                    await ReportWriter.WriteAsync(directory, report);
                }
                // Inspect the settled graph only after all writers have completed.
                foreach (var (doc, item) in results)
                {
                    if (report.EvidenceMode == "spans")
                    {
                        var offset = 0;
                        foreach (var span in ExtractionDocumentPlan.Split(doc.Content, Math.Clamp(config.GetValue("GraphRag:Ingestion:ExtractionSectionSizeChars", 2000), 256, 16000)))
                        {
                            item.SourceSpans.Add(new(SourceSpanPlan.Id(span), offset, span.Length));
                            offset += span.Length;
                        }
                    }
                    foreach (var call in item.ChatCalls.Where(c => c.ExtractionResponseJson is not null))
                    {
                        try
                        {
                            using var response = JsonDocument.Parse(call.ExtractionResponseJson!);
                            if (!response.RootElement.TryGetProperty("relationships", out var rels)) continue;
                            if (report.EvidenceMode == "spans")
                            {
                                var allowedIds = item.SourceSpans.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                                call.InvalidSourceReferences = rels.EnumerateArray().Count(e =>
                                    !e.TryGetProperty("sourceIds", out var ids) || ids.ValueKind != JsonValueKind.Array ||
                                    ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String) ||
                                    !SourceSpanPlan.ValidIds(ids.EnumerateArray().Select(id => id.GetString()!), allowedIds));
                            }
                            var names = response.RootElement.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("name").GetString()!);
                            var edges = rels.EnumerateArray().Select(e => new EvidenceEdge(
                                e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!, e.GetProperty("type").GetString()!,
                                e.TryGetProperty("evidence", out var ev) ? ev.GetString() : null)).ToArray();
                            call.EvidenceValidation = RelationshipEvidenceValidator.Validate([doc.Content], names, edges,
                                requireEvidence: report.EvidenceMode == "strict");
                        }
                        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                        {
                            call.EvidenceValidation = new(0, [new(-1, "invalid-response-shape")]);
                        }
                    }
                    if (item.Status == "Completed")
                    {
                        item.Metrics = await EvalDatabase.MetricsAsync(connectionString, item.DocumentId, cancel.Token);
                        item.ChunkVectors = await EvalDatabase.ChunkVectorsAsync(connectionString, item.DocumentId, scope, cancel.Token);
                        var previous = report.Passes.SelectMany(p => p.Documents)
                            .LastOrDefault(d => !ReferenceEquals(d, item) && d.DocumentId == item.DocumentId && d.Status == "Completed");
                        if (previous is not null)
                        {
                            var priorVectors = previous.ChunkVectors.ToDictionary(v => (v.Index, v.ContentHash), v => v.VectorHash);
                            foreach (var vector in item.ChunkVectors)
                            {
                                if (!priorVectors.TryGetValue((vector.Index, vector.ContentHash), out var prior)) continue;
                                item.UnchangedChunkVectorsChecked++;
                                if (vector.VectorHash != prior) item.UnchangedChunkVectorsChanged++;
                            }
                        }
                        item.Graph = await EvalDatabase.GraphAsync(connectionString, item.DocumentId, scope, cancel.Token);
                        item.FactChecks = (doc.Entry.ExpectedEdges ?? []).Select(label => FactualEdgeCheck.Evaluate(label, item.Graph.Edges, doc.Content)).ToList();
                        var raw = ExtractionTrace.ReadRaw(item.ChatCalls);
                        item.FactTraces = (doc.Entry.ExpectedEdges ?? []).Where(l => !l.Forbidden).Select(label =>
                            ExtractionTrace.Evaluate(label, doc.Content, raw.Names, raw.Edges, item.Graph.Edges,
                                item.ChatCalls.Any(c => c.ExtractionResponseJson is not null))).ToList();
                        item.MissingExpectedEntities = doc.Entry.ExpectedEntities.Where(term =>
                            !item.Graph.EntityNames.Any(name => name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
                        item.Passed = (report.EmbeddingCache != "on" || item.UnchangedChunkVectorsChanged == 0) &&
                            item.Graph.EmbeddedChunks == item.Chunks && item.Chunks > 0 &&
                            (mode == "vector" || (item.MissingExpectedEntities.Length == 0 && item.Graph.FactualEdges > 0 &&
                                item.Graph.Domains.Length > 0 && item.Graph.Categories.Length > 0));
                    }
                    Console.WriteLine($"  Verified {item.FileName}: gate={item.Passed}");
                    await ReportWriter.WriteAsync(directory, report);
                }
                foreach (var doc in corpus)
                {
                    var json = await search.SearchChunksAsync(new ScopedSearchRequest(doc.Entry.Query, [scope], 3), cancel.Token);
                    var result = RetrievalResult.FromJson(doc.Entry, scope, json);
                    pass.Retrieval.Add(result);
                    await ReportWriter.WriteAsync(directory, report);
                }
                Console.WriteLine($"[{mode}] Recall@3={pass.RecallAt3:P0}, MRR@3={pass.MrrAt3:F3}, passed={pass.Passed}");
            }
        }
        report.Completed = true;
        await ReportWriter.WriteAsync(directory, report);
        Console.WriteLine($"Results: {Path.Combine(directory, "report.md")}");
        return report.Passes.All(p => p.Passed) ? 0 : 2;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Evaluation canceled; completed checkpoints and scoped data are retained."); return 130; }
    catch (Exception ex) { Console.Error.WriteLine($"Evaluation error ({ex.GetType().Name}): {ex.Message}"); return 1; }
}
