using System.Text.Json;
using System.Text.RegularExpressions;
using FabrCore.Core;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Options;

namespace FabrCore.Surface.Rendering;

public sealed partial class SurfaceActionDispatcher : ISurfaceActionDispatcher
{
    private readonly ISurfaceActionRegistry actionRegistry;
    private readonly SurfaceOptions options;

    public SurfaceActionDispatcher(ISurfaceActionRegistry actionRegistry, IOptions<SurfaceOptions> options)
    {
        this.actionRegistry = actionRegistry;
        this.options = options.Value;
    }

    public async Task DispatchAsync(
        SurfaceRenderContext context,
        AdaptiveCardSurfaceAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);

        var actionType = NormalizeActionType(action.Type);
        if (!options.AllowedActionTypes.Contains(actionType))
        {
            throw new InvalidOperationException($"Adaptive Card action type '{actionType}' is not allowed.");
        }

        var payload = BuildPayload(action);
        var actionId = ResolveActionId(actionType, action, payload);
        var routeTo = ResolveRoute(context.Envelope, payload);
        var targetAgent = ResolveTargetAgent(context, payload);
        var message = BuildAgentMessage(ResolveMessageTemplate(context.Envelope, payload), payload);

        SurfaceActionResult? result = null;
        if (RoutesToApp(routeTo) && DispatchesToApplication(actionType))
        {
            result = await actionRegistry.ExecuteAsync(
                new SurfaceActionRequest
                {
                    ActionId = actionId,
                    ActionType = actionType,
                    Verb = action.Verb,
                    Envelope = context.Envelope,
                    SourceMessage = context.SourceMessage,
                    Payload = payload
                },
                cancellationToken);
        }

        if (!RoutesToAgent(routeTo) || !DispatchesToAgent(actionType))
        {
            return;
        }

        var actionEvent = new AdaptiveCardActionEvent
        {
            EnvelopeId = context.Envelope.Id,
            ActionType = actionType,
            ActionId = actionId,
            Verb = action.Verb,
            Title = action.Title,
            Url = action.Url,
            RouteTo = routeTo,
            Message = message,
            TargetAgent = targetAgent,
            Payload = payload,
            Result = result
        };

        var reply = new AgentMessage
        {
            ToHandle = actionEvent.TargetAgent,
            FromHandle = context.ClientContext.Handle,
            MessageType = SurfaceMessageTypes.UiAction,
            Message = message,
            DataType = SurfaceMessageTypes.DataType,
            Data = JsonSerializer.SerializeToUtf8Bytes(actionEvent, SurfaceJson.Options),
            Kind = MessageKind.OneWay,
            TraceId = context.SourceMessage?.TraceId
        };

        if (!string.IsNullOrWhiteSpace(reply.ToHandle))
        {
            await context.ClientContext.SendMessage(reply);
        }
    }

    private static Dictionary<string, object?> BuildPayload(AdaptiveCardSurfaceAction action)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["actionType"] = NormalizeActionType(action.Type)
        };

        if (!string.IsNullOrWhiteSpace(action.Title))
        {
            payload["title"] = action.Title;
        }

        if (!string.IsNullOrWhiteSpace(action.Verb))
        {
            payload["verb"] = action.Verb;
        }

        if (!string.IsNullOrWhiteSpace(action.Url))
        {
            payload["url"] = action.Url;
        }

        if (action.Data is { } data && data.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            payload["data"] = JsonElementToObject(data);
            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in data.EnumerateObject())
                {
                    payload[property.Name] = JsonElementToObject(property.Value);
                }
            }
        }

        if (action.Inputs.Count > 0)
        {
            payload["inputs"] = new Dictionary<string, object?>(action.Inputs, StringComparer.OrdinalIgnoreCase);
            foreach (var input in action.Inputs)
            {
                payload[input.Key] = input.Value;
            }
        }

        return payload;
    }

    private static string ResolveActionId(string actionType, AdaptiveCardSurfaceAction action, Dictionary<string, object?> payload)
    {
        if (TryGetString(payload, "actionId", out var actionId)
            || TryGetString(payload, "id", out actionId)
            || TryGetString(payload, "verb", out actionId))
        {
            return actionId;
        }

        return !string.IsNullOrWhiteSpace(action.Title) ? action.Title : actionType;
    }

    private static string ResolveRoute(AdaptiveCardSurfaceEnvelope envelope, Dictionary<string, object?> payload)
    {
        if (TryGetString(payload, "routeTo", out var routeTo))
        {
            return NormalizeRoute(routeTo);
        }

        return NormalizeRoute(envelope.Metadata?.RouteTo);
    }

    private static string? ResolveMessageTemplate(AdaptiveCardSurfaceEnvelope envelope, Dictionary<string, object?> payload)
    {
        if (TryGetString(payload, "messageTemplate", out var template))
        {
            return template;
        }

        return envelope.Metadata?.MessageTemplate;
    }

    private static string? ResolveTargetAgent(SurfaceRenderContext context, Dictionary<string, object?> payload)
    {
        if (!TryGetString(payload, "targetAgent", out var targetAgent))
        {
            targetAgent = context.Envelope.Metadata?.TargetAgent;
        }

        if (string.IsNullOrWhiteSpace(targetAgent))
        {
            return context.SourceMessage?.FromHandle;
        }

        if (targetAgent.Contains(':', StringComparison.Ordinal))
        {
            return targetAgent;
        }

        var owner = HandleUtilities.ParseHandle(context.ClientContext.Handle).Owner;
        return string.IsNullOrWhiteSpace(owner)
            ? targetAgent
            : HandleUtilities.EnsurePrefix(targetAgent, HandleUtilities.BuildPrefix(owner));
    }

    private static string NormalizeRoute(string? routeTo)
        => string.Equals(routeTo, SurfaceActionRoute.Agent, StringComparison.OrdinalIgnoreCase)
            ? SurfaceActionRoute.Agent
            : string.Equals(routeTo, SurfaceActionRoute.Both, StringComparison.OrdinalIgnoreCase)
                ? SurfaceActionRoute.Both
                : SurfaceActionRoute.App;

    private static string NormalizeActionType(string? actionType)
        => string.IsNullOrWhiteSpace(actionType) ? AdaptiveCardActionTypes.Execute : actionType;

    private static bool DispatchesToApplication(string actionType)
        => string.Equals(actionType, AdaptiveCardActionTypes.Execute, StringComparison.OrdinalIgnoreCase)
           || string.Equals(actionType, AdaptiveCardActionTypes.Submit, StringComparison.OrdinalIgnoreCase);

    private static bool DispatchesToAgent(string actionType)
        => string.Equals(actionType, AdaptiveCardActionTypes.Execute, StringComparison.OrdinalIgnoreCase)
           || string.Equals(actionType, AdaptiveCardActionTypes.Submit, StringComparison.OrdinalIgnoreCase);

    private static bool RoutesToApp(string routeTo)
        => string.Equals(routeTo, SurfaceActionRoute.App, StringComparison.OrdinalIgnoreCase)
           || string.Equals(routeTo, SurfaceActionRoute.Both, StringComparison.OrdinalIgnoreCase);

    private static bool RoutesToAgent(string routeTo)
        => string.Equals(routeTo, SurfaceActionRoute.Agent, StringComparison.OrdinalIgnoreCase)
           || string.Equals(routeTo, SurfaceActionRoute.Both, StringComparison.OrdinalIgnoreCase);

    private static string? BuildAgentMessage(string? template, Dictionary<string, object?> payload)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        return TemplateTokenRegex().Replace(
            template,
            match =>
            {
                var name = match.Groups["name"].Value;
                return TryGetPath(payload, name, out var value)
                    ? value?.ToString() ?? string.Empty
                    : match.Value;
            });
    }

    private static bool TryGetString(Dictionary<string, object?> payload, string key, out string value)
    {
        if (payload.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw?.ToString()))
        {
            value = raw.ToString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPath(Dictionary<string, object?> payload, string path, out object? result)
    {
        object? current = payload;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryGetValue(current, part, out current))
            {
                result = null;
                return false;
            }
        }

        result = current;
        return true;
    }

    private static bool TryGetValue(object? value, string key, out object? result)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly
            && readOnly.TryGetValue(key, out result))
        {
            return true;
        }

        if (value is IDictionary<string, object?> dictionary
            && dictionary.TryGetValue(key, out result))
        {
            return true;
        }

        if (value is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(key, out var property))
        {
            result = JsonElementToObject(property);
            return true;
        }

        result = null;
        return false;
    }

    private static object? JsonElementToObject(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            _ => element.Clone()
        };

    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9_.-]+)\}")]
    private static partial Regex TemplateTokenRegex();
}
