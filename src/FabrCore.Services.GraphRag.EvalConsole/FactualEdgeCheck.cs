using System.Text.RegularExpressions;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed record FactualEdgeLabel(string Id, string Fact, string SourceSection,
    string EvidenceText, string FromPattern, string[] Types, string ToPattern, bool Symmetric = false,
    string? DescriptionPattern = null, bool Forbidden = false, string? EvidencePattern = null);

internal sealed record FactualEdgeCheck(string Id, string Fact, string SourceSection,
    bool SourceEvidencePresent, List<EdgeSnapshot> Matches)
{
    public bool Forbidden { get; init; }
    public List<EdgeSnapshot> OtherRepresentations { get; init; } = [];
    public List<EdgeSnapshot> DirectionConflicts { get; init; } = [];
    public bool Passed => SourceEvidencePresent && (Forbidden ? Matches.Count == 0 : Matches.Count > 0);
    public bool Consistent => Passed && DirectionConflicts.Count == 0;

    public static FactualEdgeCheck Evaluate(FactualEdgeLabel label, List<EdgeSnapshot> edges, string source)
    {
        static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
        static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var evidencePresent = Normalize(source).Contains(Normalize(label.EvidenceText), StringComparison.OrdinalIgnoreCase);
        var matches = edges.Where(e => label.Types.Contains(e.Type, StringComparer.OrdinalIgnoreCase) &&
            (label.DescriptionPattern is null || Match(e.Description ?? "", label.DescriptionPattern)) &&
            ((Match(e.From, label.FromPattern) && Match(e.To, label.ToPattern)) ||
             (label.Symmetric && Match(e.To, label.FromPattern) && Match(e.From, label.ToPattern)))).ToList();
        // Candidates are diagnostics only: endpoint overlap or description text alone
        // cannot establish a correct typed fact.
        var candidates = edges.Where(e => !matches.Contains(e) &&
            ((Match(e.From, label.FromPattern) && Match(e.To, label.ToPattern)) ||
             (Match(e.To, label.FromPattern) && Match(e.From, label.ToPattern)) ||
             (label.EvidencePattern is not null && Match(e.Description ?? "", label.EvidencePattern)))).ToList();
        return new(label.Id, label.Fact, label.SourceSection, evidencePresent, matches)
        {
            Forbidden = label.Forbidden, OtherRepresentations = candidates,
            DirectionConflicts = label.Symmetric || label.Forbidden ? [] : edges.Where(e =>
                label.Types.Contains(e.Type, StringComparer.OrdinalIgnoreCase) &&
                Match(e.From, label.ToPattern) && Match(e.To, label.FromPattern)).ToList()
        };
    }
}
