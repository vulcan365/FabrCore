using System.Text.Json;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.GraphRag.Services;

internal enum ExtractionResponseKind { Combined, Graph, Taxonomy, EndpointRepair }

internal static class ExtractionResponseSchema
{
    public static ChatResponseFormat Create(ExtractionResponseKind kind, int descriptionTargetChars = 0, bool includeEvidence = false, bool includeSourceIds = false, IReadOnlyList<string>? allowedSourceIds = null, bool structuredTaxonomyNames = false, bool policyRelations = false, bool policyObligations = false)
    {
        object Text() => new { type = "string" };
        object Description() => descriptionTargetChars > 0
            ? new { type = "string", description = $"Aim for at most {descriptionTargetChars} characters. Preserve essential facts and identifiers; do not omit entities or relationships to shorten descriptions." } : Text();
        object Number() => new { type = "number" };
        object Obj(Dictionary<string, object> properties) => new
        {
            type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false
        };
        object ArrayOf(object items) => new { type = "array", items };
        var properties = new Dictionary<string, object>();
        if (kind is ExtractionResponseKind.Combined or ExtractionResponseKind.Taxonomy)
        {
            var taxonomy = Obj(new()
            {
                ["name"] = structuredTaxonomyNames ? new { type = "string", description = "Subject label only. When isNew=false, copy an existing name value exactly; domain metadata and descriptions are not part of the name." } : Text(), ["description"] = Description(),
                ["isNew"] = new { type = "boolean" }, ["confidence"] = Number()
            });
            properties["domain"] = taxonomy;
            properties["category"] = taxonomy;
        }
        if (kind != ExtractionResponseKind.Taxonomy)
        {
            properties["entities"] = ArrayOf(Obj(new()
            {
                ["name"] = Text(), ["entityType"] = Text(), ["description"] = Description()
            }));
            var relationship = new Dictionary<string, object>()
            {
                ["from"] = Text(), ["to"] = Text(), ["type"] = policyRelations
                    ? new { type = "string", @enum = policyObligations ? PolicyObligation.ActionTypes : PolicyRelationGuidance.Types } : Text(),
                ["description"] = Description(), ["confidence"] = Number()
            };
            if (policyObligations) relationship["obligation"] = new { type = "string", @enum = PolicyObligation.Values };
            if (includeEvidence) relationship["evidence"] = Text();
            if (includeSourceIds) relationship["sourceIds"] = ArrayOf(allowedSourceIds is { Count: > 0 }
                ? new { type = "string", @enum = allowedSourceIds.Distinct(StringComparer.Ordinal).ToArray() } : Text());
            if (kind != ExtractionResponseKind.EndpointRepair) properties["relationships"] = ArrayOf(Obj(relationship));
        }
        if (kind == ExtractionResponseKind.EndpointRepair) properties["unresolvedNames"] = ArrayOf(Text());
        return ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(Obj(properties)),
            $"graphrag_{kind.ToString().ToLowerInvariant()}_{(kind == ExtractionResponseKind.EndpointRepair ? "spans_v2" : includeSourceIds ? (allowedSourceIds is { Count: > 0 } ? "spans_v2" : "spans_v1") : includeEvidence ? "evidence_v1" : "v1")}");
    }
}
