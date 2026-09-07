namespace FabrCore.Services.GraphRag.Services;

internal static class PolicyRelationGuidance
{
    public static readonly string[] Types = ["ESTABLISHES", "USES", "PART_OF", "PUBLISHED_BY", "AUTHORED_BY",
        "REFERENCES", "REQUIRES", "RECOMMENDS", "PROHIBITS", "RESPONSIBLE_FOR", "IMPLEMENTS",
        "REPORTS_TO", "FUNDS", "GOVERNS", "DEFINES", "DEPENDS_ON", "ALIAS_OF", "RELATED_TO"];

    public const string Text = """
        Policy relationship contract (v1; overrides generic type examples below):
        Use only the relationship types allowed by the response schema. Preserve explicit
        actors and objects as named entities; from and to must exactly match entity names
        emitted in this response. Read each edge as 'from TYPE to'.
        ESTABLISHES: creating actor -> created body, program or resource.
        USES: using actor -> resource, method, format, metadata or license it uses.
        PART_OF: component -> containing organization. PUBLISHED_BY: document -> publisher.
        AUTHORED_BY: document -> author, only when authorship is explicitly stated.
        REFERENCES: citing document -> cited work. Citation does not establish authorship.
        RESPONSIBLE_FOR: responsible actor -> explicit activity or responsibility.
        IMPLEMENTS: implementing actor -> policy or program implemented.
        REPORTS_TO: reporting actor -> recipient. FUNDS: funder -> funded object.
        GOVERNS: governing authority/policy -> governed object. DEFINES: work -> defined term.
        REQUIRES, RECOMMENDS, PROHIBITS: issuing rule/authority -> stated action or object.
        DEPENDS_ON: dependent -> prerequisite. ALIAS_OF: alternate name -> canonical name.
        RELATED_TO is a last resort for an explicit relation that fits none of these types.
        Prefer a direct actor-action-object fact when the source identifies the actor;
        do not replace that actor with the document that describes its responsibility.
        Preserve all explicit relevant facts, including organizational membership, creation,
        publication, technology use and policy responsibilities. Never invent a fact to
        fill a relationship type. Include both endpoints; do not omit them as implicit.
        Descriptions must preserve obligation strength: must/shall are requirements,
        should/encouraged are recommendations, and may is permission. Preserve exceptions,
        conditions, scope and future intentions. Do not turn a proposed action into a
        completed action or a recommendation into a requirement. Describe prohibition
        explicitly, never as an affirmative USES edge. Use source wording for qualifiers.

        """;
}
