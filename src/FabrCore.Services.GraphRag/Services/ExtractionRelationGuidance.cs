namespace FabrCore.Services.GraphRag.Services;

internal static class ExtractionRelationGuidance
{
    public const string Text = """
        Graph relationship conventions (v1; these extend the type examples below):
        - Read every directed edge as "from TYPE to". AUTHORED_BY points from a work
          to its author; SIGNED_BY from a work to its signer. PUBLISHED_BY is an
          additional allowed type, from a work/standard to its publisher. Publication
          does not establish authorship. Never reverse these edges.
        - USES points from a user/system/format to the technology, encoding, algorithm,
          or namespace it uses. DEPENDS_ON points from the dependent to its prerequisite.
          ESTABLISHES points from the establishing authority to the thing established.
          PART_OF points from a component to its containing whole.
        - Technical requirements should connect the subject format, protocol, or
          concept directly to the required technology. The specification is evidence,
          not a substitute for that subject. Include both endpoint entities. Preserve
          conditions, optionality, and version qualifiers in the description.
        - REFERENCES is a citation from the citing work to the cited work. Do not
          turn a citation into a dependency of a mentioned concept without evidence.
        - When two distinct names are explicitly stated to denote the same thing,
          ALIAS_OF is an additional allowed type, from the alternate name to the
          canonical name used in the source. Preserve both named endpoints for this
          edge. Similarity and co-occurrence do not establish alias equivalence.
        - Preserve specific names and identifiers; use one consistent name per
          endpoint within a response. Do not replace a specific technology with a
          generic class or treat a document's mention as proof of a technical dependency.
        - Do not emit positive edges for negated or prohibited facts or unsupported
          speculation. Keep explicitly stated optional uses qualified as optional.
          If support is absent, omit the edge. Do not invent facts
          to populate a relationship type. RELATED_TO must still have explicit support.

        """;
}
