using System.Text.RegularExpressions;

namespace FabrCore.Services.GraphRag.Services;

// Resolve only explicit parenthesized initialisms whose capital letters agree with
// the expansion. No fuzzy matching, plural stripping, or invented entities.
internal sealed class ExtractionEndpointResolver
{
    private readonly HashSet<string> _names;
    private readonly Dictionary<string, string[]> _aliases;
    private static readonly Regex Pair = new(@"^(?<left>[^()]+)\s+\((?<right>[^()]+)\)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Initialism = new(@"^[A-Z]{2,}s?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public ExtractionEndpointResolver(IEnumerable<string> entityNames)
    {
        _names = entityNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<(string Alias, string Name)>();
        foreach (var name in _names)
        {
            var pair = Pair.Match(name);
            if (!pair.Success) continue;
            var left = pair.Groups["left"].Value.Trim();
            var right = pair.Groups["right"].Value.Trim();
            Add(left, right, name);
            Add(right, left, name);
        }
        _aliases = aliases.GroupBy(a => a.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        void Add(string alias, string expansion, string name)
        {
            if (!Initialism.IsMatch(alias) || !expansion.Any(char.IsWhiteSpace)) return;
            var capitals = string.Concat(expansion.Where(c => c is >= 'A' and <= 'Z'));
            if (string.Equals(alias.EndsWith('s') ? alias[..^1] : alias, capitals, StringComparison.Ordinal))
                aliases.Add((alias, name));
        }
    }

    public string Resolve(string endpoint)
        => !_names.Contains(endpoint) && _aliases.TryGetValue(endpoint, out var matches) && matches.Length == 1
            ? matches[0] : endpoint;
}
