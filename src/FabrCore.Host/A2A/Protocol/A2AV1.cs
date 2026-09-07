using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace FabrCore.Host.A2A.Protocol;

/// <summary>Wire adapter for A2A 1.0 (specification release 1.0.1).</summary>
internal static class A2AV1
{
    public const string SpecificationVersion = "1.0.1";
    public static JsonElement Parameters(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText());
        if (node is not JsonObject root) return element;
        if (root["message"] is JsonObject message)
        {
            if (message["role"] is JsonValue role && role.TryGetValue<string>(out var roleName))
                message["role"] = roleName switch { "ROLE_USER" => "user", "ROLE_AGENT" => "agent", var value => value };
            if (message["parts"] is JsonArray parts)
                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (part.ContainsKey("text")) part["kind"] = "text";
                    else if (part.ContainsKey("data")) part["kind"] = "data";
                    else if (part.ContainsKey("raw") || part.ContainsKey("url"))
                    {
                        part["kind"] = "file";
                        part["file"] = new JsonObject
                        {
                            ["bytes"] = part["raw"]?.DeepClone(), ["uri"] = part["url"]?.DeepClone(),
                            ["mimeType"] = part["mediaType"]?.DeepClone(), ["name"] = part["filename"]?.DeepClone(),
                        };
                    }
                }
        }
        if (root["configuration"] is JsonObject config)
        {
            if (config["returnImmediately"] is JsonValue immediate)
            {
                if (immediate.TryGetValue<bool>(out var flag)) config["blocking"] = !flag;
                else config["blocking"] = immediate.DeepClone(); // Let typed validation reject malformed values.
            }
            else config["blocking"] = true;
            if (config["taskPushNotificationConfig"] is { } push)
                config["pushNotificationConfig"] = push.DeepClone();
        }
        return JsonSerializer.SerializeToElement(root, A2AJson.Options);
    }

    public static object Result(object value, bool wrap = false)
    {
        var node = JsonSerializer.SerializeToNode(value, A2AJson.Options)!;
        Transform(node);
        var key = value switch
        {
            A2ATask => "task", A2AMessage => "message",
            A2ATaskStatusUpdateEvent => "statusUpdate", A2ATaskArtifactUpdateEvent => "artifactUpdate",
            _ => null,
        };
        return wrap && key is not null ? new JsonObject { [key] = node } : node;
    }

    private static void Transform(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child is not null) Transform(child);
            return;
        }
        if (node is not JsonObject obj) return;
        obj.Remove("kind");
        obj.Remove("final");
        if (obj["role"] is JsonValue role) obj["role"] = "ROLE_" + role.GetValue<string>().ToUpperInvariant();
        if (obj["state"] is JsonValue state) obj["state"] = "TASK_STATE_" + state.GetValue<string>().Replace('-', '_').ToUpperInvariant();
        if (obj.Remove("file", out var file) && file is JsonObject f)
        {
            if (f["bytes"] is { } bytes) obj["raw"] = bytes.DeepClone();
            if (f["uri"] is { } uri) obj["url"] = uri.DeepClone();
            if (f["name"] is { } name) obj["filename"] = name.DeepClone();
            if (f["mimeType"] is { } mime) obj["mediaType"] = mime.DeepClone();
        }
        // Only traverse protocol fields. User data and metadata remain opaque.
        foreach (var field in new[] { "parts", "history", "artifacts", "status", "message", "artifact", "tasks" })
            if (obj[field] is { } child) Transform(child);
    }

    public static object Card(A2AAgentCard card)
    {
        var node = JsonSerializer.SerializeToNode(card, A2AJson.Options)!.AsObject();
        node.Remove("protocolVersion"); node.Remove("url"); node.Remove("preferredTransport");
        node.Remove("additionalInterfaces"); node.Remove("supportsAuthenticatedExtendedCard");
        node["supportedInterfaces"] = new JsonArray(
            new JsonObject { ["url"] = card.Url, ["protocolBinding"] = "JSONRPC", ["protocolVersion"] = "1.0" },
            new JsonObject { ["url"] = card.Url, ["protocolBinding"] = "HTTP+JSON", ["protocolVersion"] = "1.0" });
        node["capabilities"]!.AsObject().Remove("stateTransitionHistory");
        node["capabilities"]!["extendedAgentCard"] = false;
        if (node.Remove("security", out var security) && security is JsonArray requirements)
        {
            var mapped = new JsonArray();
            foreach (var requirement in requirements.OfType<JsonObject>())
            {
                var schemes = new JsonObject();
                foreach (var pair in requirement)
                    schemes[pair.Key] = new JsonObject { ["list"] = pair.Value?.DeepClone() };
                mapped.Add(new JsonObject { ["schemes"] = schemes });
            }
            node["securityRequirements"] = mapped;
        }
        if (node["securitySchemes"] is JsonObject securitySchemes)
            foreach (var pair in securitySchemes.ToList())
            {
                var scheme = pair.Value!.DeepClone().AsObject();
                var type = scheme["type"]!.GetValue<string>();
                scheme.Remove("type");
                if (scheme.Remove("in", out var location)) scheme["location"] = location;
                var key = type switch { "apiKey" => "apiKeySecurityScheme", "oauth2" => "oauth2SecurityScheme", _ => "httpAuthSecurityScheme" };
                securitySchemes[pair.Key] = new JsonObject { [key] = scheme };
            }
        return node;
    }
}
