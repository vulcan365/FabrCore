using System.Text.Json;
using System.Text.RegularExpressions;

namespace FabrCore.Services.GraphRag.Services;

internal sealed record EvidenceEdge(string From, string To, string Type, string? Evidence);
internal sealed record EvidenceIssue(int EdgeIndex, string Reason);
internal sealed record EvidenceValidation(int EdgeCount, List<EvidenceIssue> Issues)
{
    public bool Passed => Issues.Count == 0;
}

internal static class RelationshipEvidenceValidator
{
    public const string Guidance = """
        For every relationship, add an "evidence" string containing a short verbatim
        passage from CONTENT TO ANALYZE that supports the relationship. Whitespace
        may be normalized, but do not paraphrase, splice passages, or use metadata.
        Include enough context to retain negation, direction, conditions, and names.
        Include both endpoint entities. Do not emit both directions of the same
        asymmetric relationship between a pair. Omit unsupported relationships.
        """;

    // Opposite edges of these predicates contradict this extraction contract.
    // USES/DEPENDS_ON/REFERENCES are deliberately excluded: reciprocal uses,
    // dependencies and citations can be real rather than mistakes.
    private static readonly HashSet<string> Asymmetric = new(StringComparer.OrdinalIgnoreCase)
        { "AUTHORED_BY", "PUBLISHED_BY", "SIGNED_BY", "ESTABLISHES", "PART_OF" };

    public static EvidenceValidation Validate(IReadOnlyList<string> sources, IEnumerable<string> entityNames,
        IReadOnlyList<EvidenceEdge> edges, bool requireEvidence = true)
    {
        static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
        var normalizedSources = sources.Select(Normalize).ToArray();
        var names = entityNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = edges.Select(e => $"{e.From}\u001f{e.To}\u001f{e.Type}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = new List<EvidenceIssue>();
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            if (!names.Contains(edge.From) || !names.Contains(edge.To)) issues.Add(new(i, "missing-endpoint"));
            if (requireEvidence)
            {
                var quote = Normalize(edge.Evidence ?? "");
                if (quote.Length == 0) issues.Add(new(i, "missing-evidence"));
                else if (!normalizedSources.Any(s => s.Contains(quote, StringComparison.Ordinal)))
                    issues.Add(new(i, "evidence-not-in-source"));
            }
            if (Asymmetric.Contains(edge.Type) && keys.Contains($"{edge.To}\u001f{edge.From}\u001f{edge.Type}"))
                issues.Add(new(i, "contradictory-direction"));
        }
        return new(edges.Count, issues);
    }
}
