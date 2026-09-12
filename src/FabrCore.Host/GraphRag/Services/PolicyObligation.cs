namespace FabrCore.Services.GraphRag.Services;

internal static class PolicyObligation
{
    public static readonly string[] Values = ["required", "recommended", "permitted", "prohibited", "none"];
    public static readonly string[] ActionTypes = PolicyRelationGuidance.Types.Where(t => t is not ("REQUIRES" or "RECOMMENDS")).ToArray();
    public const string Guidance = """
        Separate obligation from action (overrides policy v1 obligation/type guidance):
        Each relationship must include obligation: required, recommended, permitted,
        prohibited, or none. Use required for must/shall, recommended for should or
        encouraged, permitted for permission, prohibited for explicit prohibition, and
        none for a descriptive fact without a normative obligation. Possibility is not
        permission. Preserve future intentions, conditions and exceptions in description.
        Type expresses the action, not its strength: an actor that must use a resource
        has type USES and obligation required; should use is USES/recommended.
        REQUIRES and RECOMMENDS are not action types in this schema. For prohibitions
        retain PROHIBITS with obligation prohibited; do not encode an affirmative USES
        edge for a forbidden action. Include the actor and object as exact entity names.
        Preserve distinct recommendations and permissions as well as requirements.
        Description must retain the source's own obligation wording and conditions;
        the obligation field is additional metadata, not a replacement for source detail.
        """;

    // Experimental storage envelope; keep source description separate when evaluating facts.
    public static string? Store(string? description, string? obligation)
        => obligation is null ? description : description + "\n[GraphRAG obligation v1: " + obligation + "]";

    public static (string? Description, string? Obligation) Read(string? stored)
    {
        foreach (var value in Values)
        {
            var suffix = "\n[GraphRAG obligation v1: " + value + "]";
            if (stored?.EndsWith(suffix, StringComparison.Ordinal) == true)
                return (stored[..^suffix.Length], value);
        }
        return (stored, null);
    }
}
