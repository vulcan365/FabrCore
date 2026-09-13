using System.Text.Json;
using System.Text.RegularExpressions;
using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed record FactualEdgeTrace(string Id, string Outcome, bool SourceEvidencePresent,
    bool RawConsistent, bool PersistedConsistent, List<EdgeSnapshot> RawMatches,
    List<EdgeSnapshot> RawCandidates, string[] MissingEndpointNames, string[] ExpectedFromEntities, string[] ExpectedToEntities);

internal static class ExtractionTrace
{
    public static (string[] Names, List<EdgeSnapshot> Edges) ReadRaw(IEnumerable<ChatCallSample> calls)
    {
        var names = new List<string>();
        var edges = new List<EdgeSnapshot>();
        foreach (var call in calls)
        {
            if (call.ExtractionResponseJson is null) continue;
            try
            {
                using var json = JsonDocument.Parse(call.ExtractionResponseJson);
                if (json.RootElement.TryGetProperty("entities", out var entities))
                    names.AddRange(entities.EnumerateArray().Select(e => e.GetProperty("name").GetString()!));
                if (json.RootElement.TryGetProperty("relationships", out var relationships))
                    edges.AddRange(JsonSerializer.Deserialize<List<EdgeSnapshot>>(relationships.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        return (names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), edges);
    }

    public static FactualEdgeTrace Evaluate(FactualEdgeLabel label, string source, string[] names,
        List<EdgeSnapshot> rawEdges, List<EdgeSnapshot> persistedEdges, bool rawAvailable)
    {
        var raw = FactualEdgeCheck.Evaluate(label, rawEdges, source);
        var stored = FactualEdgeCheck.Evaluate(label, persistedEdges, source);
        var nameSet = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = raw.Matches.SelectMany(e => new[] { e.From, e.To })
            .Where(n => !nameSet.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var outcome = !stored.SourceEvidencePresent ? "source-evidence-missing"
            : stored.Consistent ? "persisted-match"
            : !rawAvailable ? "raw-unavailable"
            : stored.DirectionConflicts.Count > 0 ? "persisted-direction-conflict"
            : raw.Matches.Count > 0 ? missing.Length > 0 ? "raw-match-missing-endpoint" : "raw-match-not-persisted"
            : raw.OtherRepresentations.Count > 0 ? "alternative-representation" : "required-edge-not-emitted";
        return new(label.Id, outcome, raw.SourceEvidencePresent, raw.Consistent, stored.Consistent,
            raw.Matches, raw.OtherRepresentations, missing,
            names.Where(n => Regex.IsMatch(n, label.FromPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToArray(),
            names.Where(n => Regex.IsMatch(n, label.ToPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToArray());
    }

    // Offline prediction only: does not simulate SQL merging or rewrite stored graphs.
    public static List<EdgeSnapshot> ResolveAliases(string[] names, List<EdgeSnapshot> edges)
    {
        var resolver = new ExtractionEndpointResolver(names);
        var known = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return edges.Select(e => e with { From = resolver.Resolve(e.From), To = resolver.Resolve(e.To) })
            .Where(e => known.Contains(e.From) && known.Contains(e.To)).ToList();
    }
}
