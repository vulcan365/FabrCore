using System.Text.Json;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed record TraceDocument(string Run, int Iteration, string Scope, string File, List<FactualEdgeTrace> Traces,
    List<FactualEdgeCheck> AliasPrediction, int ResolvedEndpointReferences);

internal static class TraceReport
{
    public static async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        var input = Path.GetFullPath(options.Value("input", "report.json"));
        var output = Path.GetFullPath(options.Value("output", "artifacts/graphrag-fact-trace"));
        var corpus = await Corpus.LoadAsync(options.Value("manifest", Path.Combine(AppContext.BaseDirectory, "corpus-relations-v3.json")),
            options.Value("cache", "artifacts/graphrag-corpus"), options.Number("max-documents", 3, 1, 100),
            options.Number("max-chars", 30000, 0, 5_000_000), ct);
        using var inputJson = JsonDocument.Parse(await File.ReadAllTextAsync(input, ct));
        var paths = inputJson.RootElement.ValueKind == JsonValueKind.Array
            ? inputJson.RootElement.EnumerateArray().Select(r => Path.Combine(Path.GetDirectoryName(input)!, r.GetProperty("run").GetString()!, "report.json")).ToArray()
            : [input];
        var results = new List<TraceDocument>();
        foreach (var path in paths)
        {
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            foreach (var pass in report.RootElement.GetProperty("Passes").EnumerateArray())
            foreach (var row in pass.GetProperty("Documents").EnumerateArray())
            {
                var doc = JsonSerializer.Deserialize<DocumentResult>(row.GetRawText())!;
                if (doc.Status != "Completed" || doc.Graph is null) throw new InvalidOperationException("Trace requires completed graph snapshots.");
                var source = corpus.Single(c => c.Entry.FileName == doc.FileName);
                if (!string.Equals(source.Sha256, doc.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Trace source hash does not match the ingested content; supply the exact corpus.");
                if (doc.ChatCalls.Count == 0 || doc.ChatCalls.Any(c => c.ExtractionResponseJson is null))
                    throw new InvalidOperationException("Trace requires captured raw responses; cached or uncaptured runs cannot support this replay.");
                var raw = ExtractionTrace.ReadRaw(doc.ChatCalls);
                var prediction = ExtractionTrace.ResolveAliases(raw.Names, raw.Edges);
                var labels = source.Entry.ExpectedEdges ?? [];
                var traces = labels.Where(l => !l.Forbidden).Select(l =>
                    ExtractionTrace.Evaluate(l, source.Content, raw.Names, raw.Edges, doc.Graph.Edges, true)).ToList();
                var resolver = new FabrCore.Services.GraphRag.Services.ExtractionEndpointResolver(raw.Names);
                var resolved = raw.Edges.SelectMany(e => new[] { e.From, e.To }).Count(n => resolver.Resolve(n) != n);
                results.Add(new(report.RootElement.GetProperty("RunId").GetString()!, pass.GetProperty("Iteration").GetInt32(),
                    pass.GetProperty("Scope").GetString()!, doc.FileName, traces,
                    labels.Select(l => FactualEdgeCheck.Evaluate(l, prediction, source.Content)).ToList(), resolved));
            }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "trace.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), ct);
        foreach (var group in results.SelectMany(d => d.Traces).GroupBy(t => t.Outcome)) Console.WriteLine($"{group.Key}: {group.Count()}");
        Console.WriteLine($"Traces: {Path.Combine(output, "trace.json")}. Alias results are offline predictions, not persisted changes.");
        return 0;
    }
}
