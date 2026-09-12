using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FabrCore.Services.GraphRag.EvalConsole;

/// <summary>Eval-only format override for providers supporting JSON mode but not JSON Schema.</summary>
internal sealed class JsonObjectChatClient(IChatClient inner, bool guided = false, bool normalize = false) : DelegatingChatClient(inner)
{
    internal const string RawResponseKey = "eval.rawProviderResponse";
    internal const string Guidance = """
        You extract facts from documents, not solve math problems. Return only the requested JSON object.
        Follow the user's exact object structure. entities and relationships must be flat arrays of objects,
        never nested arrays or strings. Each entity has name, entityType, description.
        Each relationship has from, to, type, description, and numeric confidence between 0 and 1.
        When domain/category are requested, each is an object with name, description, boolean isNew,
        and numeric confidence between 0 and 1. Use empty arrays when no facts are present.
        Preserve source names and identifiers exactly. Do not invent or shorten RFC numbers.
        Extract only facts stated in the source, and keep descriptions concise.
        """;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = options?.Clone() ?? new ChatOptions();
        request.ResponseFormat = ChatResponseFormat.Json;
        if (guided)
        {
            request.Temperature = 0;
            messages = new[] { new ChatMessage(ChatRole.System, Guidance) }.Concat(messages);
        }
        var response = await base.GetResponseAsync(messages, request, cancellationToken);
        if (!normalize || NormalizeArrays(response.Text) is not { } normalized) return response;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, normalized))
        {
            Usage = response.Usage, ModelId = response.ModelId, FinishReason = response.FinishReason,
            ResponseId = response.ResponseId,
            AdditionalProperties = new() { [RawResponseKey] = response.Text }
        };
    }

    // Only remove array nesting. Never drop invalid values, rename identifiers, or invent fields.
    internal static string? NormalizeArrays(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root) return null;
            var changed = false;
            foreach (var key in new[] { "entities", "relationships" })
            {
                if (root[key] is not JsonArray array || !array.Any(item => item is JsonArray)) continue;
                var flattened = new JsonArray();
                if (!Flatten(array, flattened)) return null;
                root[key] = flattened;
                changed = true;
            }
            return changed ? root.ToJsonString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Flatten(JsonArray source, JsonArray destination)
    {
        foreach (var item in source)
        {
            if (item is JsonArray nested)
            {
                if (!Flatten(nested, destination)) return false;
            }
            else if (item is JsonObject obj) destination.Add(obj.DeepClone());
            else return false;
        }
        return true;
    }
}
