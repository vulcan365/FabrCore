using System.Text;
using System.Text.Json;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed record SourceInfo(string FileName, string Url, string ContentSha256, string SourceSha256, int IngestedCharacters, int SourceCharacters);
internal sealed record EdgeSnapshot(string From, string Type, string To, string? Description)
{
    public string? Obligation { get; init; }
}
internal sealed record GraphSnapshot(string[] EntityNames, List<EdgeSnapshot> Edges, string[] Domains, string[] Categories, int EmbeddedChunks)
{
    public int FactualEdges => Edges.Count;
}

internal sealed record SourceSpanLocation(string Id, int Start, int Length);
internal sealed record DocumentResult(string FileName, Guid DocumentId, string Status, long WallMs, int Chunks, int Entities, int Relationships, string? Error)
{
    public int UnchangedChunkVectorsChecked { get; set; }
    public int UnchangedChunkVectorsChanged { get; set; }
    public long EmbeddingCacheHits { get; set; }
    public List<EmbeddingCallSample> EmbeddingCalls { get; set; } = [];
    public List<ChunkVectorFingerprint> ChunkVectors { get; set; } = [];
    public string? ExtractionInstructions { get; set; }
    public List<FactualEdgeTrace> FactTraces { get; set; } = [];
    public long ExtractionCacheHits { get; set; }
    public string? ContentSha256 { get; set; }
    public bool Passed { get; set; }
    public List<SourceSpanLocation> SourceSpans { get; set; } = [];
    public List<FactualEdgeCheck> FactChecks { get; set; } = [];
    public List<ChatCallSample> ChatCalls { get; set; } = [];
    public Dictionary<string, object?> Metrics { get; set; } = [];
    public GraphSnapshot? Graph { get; set; }
    public string[] MissingExpectedEntities { get; set; } = [];
}

internal sealed record RetrievalResult(string Query, string ExpectedFile, int Rank, bool ScopeIsolated, bool EvidenceHit, string RawResult, string? Error)
{
    public static RetrievalResult FromJson(CorpusEntry entry, string scope, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement.EnumerateArray().ToArray();
            var rank = Array.FindIndex(rows, r => r.GetProperty("entityName").GetString() == entry.FileName) + 1;
            var isolated = rows.All(r => r.GetProperty("scope").GetString() == scope);
            var evidence = rows.Where(r => r.GetProperty("entityName").GetString() == entry.FileName)
                .Any(r => entry.EvidenceTerms.All(term => (r.GetProperty("content").GetString() ?? "")
                    .Contains(term, StringComparison.OrdinalIgnoreCase)));
            return new(entry.Query, entry.FileName, rank, isolated, evidence, json, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(entry.Query, entry.FileName, 0, false, false, json, "Retrieval did not return a valid result array.");
        }
    }
}

internal sealed record EvalPass(string Mode, int Iteration, string Scope)
{
    public long? IngestionWallMs { get; set; }
    public int? PeakChatConcurrency { get; set; }
    public int TaxonomyRowsAtStart { get; set; }
    public List<DocumentResult> Documents { get; } = [];
    public List<RetrievalResult> Retrieval { get; } = [];
    public double RecallAt3 => Retrieval.Count == 0 ? 0 : Retrieval.Count(r => r.Rank > 0) / (double)Retrieval.Count;
    public double MrrAt3 => Retrieval.Count == 0 ? 0 : Retrieval.Average(r => r.Rank > 0 ? 1d / r.Rank : 0);
    public bool Passed => Documents.Count > 0 && Documents.All(d => d.Passed) && Retrieval.Count == Documents.Count
        && RecallAt3 == 1 && MrrAt3 >= 0.70 && Retrieval.All(r => r.ScopeIsolated && r.Error is null);
}

internal sealed record EvalReport(string RunId, DateTimeOffset StartedAt, string Server, string Database,
    string ModelAlias, string Provider, string Model, string EmbeddingModel, List<SourceInfo> Corpus)
{
    public string TaxonomyNames { get; set; } = "current";
    public int DocumentConcurrency { get; set; } = 1;
    public string EndpointAliases { get; set; } = "off";
    public int? SectionsPerBatch { get; set; }
    public string TaxonomyCache { get; set; } = "off";
    public string EmbeddingCache { get; set; } = "off";
    public string ResultCache { get; set; } = "off";
    public string Reingest { get; set; } = "fresh";
    public string RepairMode { get; set; } = "off";
    public string EvidenceMode { get; set; } = "off";
    public string RelationConventions { get; set; } = "current";
    public string ResponseFormat { get; set; } = "prompt";
    public int DescriptionTargetChars { get; set; }
    public bool Completed { get; set; }
    public List<EvalPass> Passes { get; } = [];
}

internal static class ReportWriter
{
    public static async Task WriteAsync(string directory, EvalReport report)
    {
        // Windows readers/virus scanners can briefly block replacement of a checkpoint.
        for (var attempt = 0; ; attempt++)
        {
            try { await WriteCoreAsync(directory, report); return; }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            { await Task.Delay(50 << attempt); }
        }
    }

    private static async Task WriteCoreAsync(string directory, EvalReport report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json.tmp"), json);
        File.Move(Path.Combine(directory, "report.json.tmp"), Path.Combine(directory, "report.json"), overwrite: true);
        var text = new StringBuilder($"# GraphRAG evaluation {report.RunId}\n\n");
        text.AppendLine($"Database: `{report.Server}/{report.Database}`. Model: `{report.ModelAlias}` / `{report.Model}`. Embeddings: `{report.EmbeddingModel}`.");
        text.AppendLine($"Taxonomy names: {report.TaxonomyNames}.\n");
        text.AppendLine($"Document concurrency: {report.DocumentConcurrency}. Ingestion seconds measure batch wall time; historical reports without that field use summed document time.\n");
        text.AppendLine($"Explicit endpoint aliases: {report.EndpointAliases}.\n");
        text.AppendLine($"Document sections per extraction batch: {report.SectionsPerBatch?.ToString() ?? "unrecorded"}.\n");
        text.AppendLine($"Embedding cache: {report.EmbeddingCache}. Provider embedding calls: {report.Passes.Sum(p => p.Documents.Sum(d => d.EmbeddingCalls.Count))}; inputs: {report.Passes.Sum(p => p.Documents.Sum(d => d.EmbeddingCalls.Sum(c => c.Inputs)))}.\n");
        text.AppendLine($"Result cache: {report.ResultCache}; taxonomy/combined cache: {report.TaxonomyCache}; reingestion: {report.Reingest}. Cache hits: {report.Passes.Sum(p => p.Documents.Sum(d => d.ExtractionCacheHits))}.\n");
        text.AppendLine($"Evidence: `{report.EvidenceMode}`; endpoint repair: `{report.RepairMode}`; relation conventions: `{report.RelationConventions}`; response format: `{report.ResponseFormat}`; schema description character target: {report.DescriptionTargetChars} (0 = unchanged).\n");
        text.AppendLine($"\nCompleted: {report.Completed}. Runs retain their scope-owned data for inspection.\n");
        text.AppendLine("| Pipeline | Iteration | Ingestion seconds | Chat calls | Extraction cache hits | Input tokens | Output tokens | Recall@3 | MRR@3 | Passed |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var pass in report.Passes)
        {
            long Sum(string key) => pass.Documents.Sum(d => d.Metrics.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0);
            text.AppendLine($"| {pass.Mode} | {pass.Iteration} | {(pass.IngestionWallMs ?? pass.Documents.Sum(d => d.WallMs)) / 1000d:F1} | {Sum("ChatCallCount")} | {pass.Documents.Sum(d => d.ExtractionCacheHits)} | {Sum("ChatInputTokens")} | {Sum("ChatOutputTokens")} | {pass.RecallAt3:P0} | {pass.MrrAt3:F3} | {pass.Passed} |");
        }
        foreach (var pass in report.Passes)
        {
            text.AppendLine($"\n## {pass.Mode}, iteration {pass.Iteration}\n\nScope: `{pass.Scope}`. Initial taxonomy rows: {pass.TaxonomyRowsAtStart}.\n");
            text.AppendLine($"Observed peak concurrent chat requests: {pass.PeakChatConcurrency}; summed document latency: {pass.Documents.Sum(d => d.WallMs) / 1000d:F1}s.\n");
            var allChecks = pass.Documents.SelectMany(d => d.FactChecks).ToArray();
            var facts = allChecks.Where(f => !f.Forbidden).ToArray();
            var negativeChecks = allChecks.Where(f => f.Forbidden).ToArray();
            text.AppendLine($"Consistent required facts: {facts.Count(f => f.Consistent)}/{facts.Length}; reversed-direction conflicts: {facts.Sum(f => f.DirectionConflicts.Count)}.\n");
            text.AppendLine($"Negative checks: {negativeChecks.Count(f => f.Passed)}/{negativeChecks.Length} passed. Absence of these selected forbidden edges is not overall precision.\n");
            text.AppendLine($"Labeled directed facts: {facts.Count(f => f.Passed)}/{facts.Length} matched. These are separate from smoke gates; missing facts block accepting a compact variant.\n");
            foreach (var fact in allChecks) text.AppendLine($"- Fact {fact.Id}: passed={fact.Passed}, forbidden={fact.Forbidden}, source evidence present={fact.SourceEvidencePresent}, alternative candidates={fact.OtherRepresentations.Count}, direction conflicts={fact.DirectionConflicts.Count}.");
            text.AppendLine($"Embedding provider calls: {pass.Documents.Sum(d => d.EmbeddingCalls.Count)}; inputs: {pass.Documents.Sum(d => d.EmbeddingCalls.Sum(c => c.Inputs))}; cache hits: {pass.Documents.Sum(d => d.EmbeddingCacheHits)}.\n");
            foreach (var trace in pass.Documents.SelectMany(d => d.FactTraces).GroupBy(t => t.Outcome))
                text.AppendLine($"- Fact trace {trace.Key}: {trace.Count()}");
            var calls = pass.Documents.SelectMany(d => d.ChatCalls).ToArray();
            text.AppendLine($"Endpoint repair calls: {calls.Count(c => c.ResponseSchemaName?.Contains("endpointrepair") == true)}; invalid source-ID references: {calls.Sum(c => c.InvalidSourceReferences ?? 0)}.\n");
            var validations = calls.Where(c => c.EvidenceValidation is not null).Select(c => c.EvidenceValidation!).ToArray();
            text.AppendLine($"Provider-response validation: {validations.Sum(v => v.EdgeCount)} relationships, {validations.Sum(v => v.Issues.Count)} issues. Full-document quote checks here are diagnostic; ingestion validates against the actual batch and checks merged directions.\n");
            foreach (var issue in validations.SelectMany(v => v.Issues).GroupBy(i => i.Reason))
                text.AppendLine($"- Validation {issue.Key}: {issue.Count()}");
            var cacheReported = calls.Count(c => c.CachedInputTokens.HasValue);
            var cachedTokens = calls.Sum(c => c.CachedInputTokens ?? 0);
            text.AppendLine($"Provider cache telemetry: {cacheReported}/{calls.Length} calls reported cached-input usage; {cachedTokens} cached input tokens observed. Missing usage is unknown, not zero.\n");
            foreach (var doc in pass.Documents)
                text.AppendLine($"- {doc.FileName}: {doc.Status}, {doc.WallMs / 1000d:F1}s, {doc.Chunks} chunks, {doc.Entities} entities, {doc.Graph?.FactualEdges ?? 0} non-provenance edges; passed={doc.Passed}." );
            foreach (var query in pass.Retrieval)
                text.AppendLine($"- Retrieval {query.ExpectedFile}: rank={query.Rank}, evidence terms hit={query.EvidenceHit}, isolated={query.ScopeIsolated}.");
        }
        text.AppendLine("\n## Sources\n");
        foreach (var source in report.Corpus)
            text.AppendLine($"- [{source.FileName}]({source.Url}): {source.IngestedCharacters}/{source.SourceCharacters} characters, SHA256 `{source.ContentSha256}`.");
        text.AppendLine("\nThese are smoke gates, not a labeled entity/edge precision benchmark. Evidence-term hits are diagnostic. No LLM judge is used. Taxonomy is shared across scopes, so later passes can benefit from earlier classification; compare alternates order across iterations. Provider warmup, download, and schema setup are excluded from ingestion timing. Failed runs may lack persisted token metrics. See report.json for graph edges, phase timings, and raw retrieval evidence.");
        await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), text.ToString());
    }
}

internal sealed record ChunkVectorFingerprint(int Index, string ContentHash, string VectorHash);
