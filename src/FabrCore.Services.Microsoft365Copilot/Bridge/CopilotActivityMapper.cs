using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using FabrCore.Core;
using Microsoft.Agents.Core.Models;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Translates between FabrCore surface messages and Microsoft 365 Copilot / Teams activities.
/// Outbound, a reply with <c>MessageType</c> <see cref="UiRenderMessageType"/> and <c>DataType</c>
/// <see cref="SurfaceAdaptiveCardDataType"/> carries a UTF-8 JSON envelope in
/// <see cref="AgentMessage.Data"/>; the Adaptive Card inside it is re-attached as a parsed
/// <see cref="JsonElement"/> under the Microsoft adaptive-card content type so the channel
/// receives a JSON object rather than a double-encoded string. Inbound, an Adaptive Card
/// <c>Action.Submit</c> (the incoming activity's <c>Value</c>) becomes a surface
/// <see cref="UiActionMessageType"/> message whose <c>Data</c> carries the action event JSON.
/// </summary>
public static partial class CopilotActivityMapper
{
    /// <summary>FabrCore surface message type for UI render payloads.</summary>
    public const string UiRenderMessageType = "ui.render";

    /// <summary>FabrCore surface message type for UI action (card submit) payloads.</summary>
    public const string UiActionMessageType = "ui.action";

    /// <summary>Adaptive Card action type reported for channel card submits.</summary>
    public const string SubmitActionType = "Action.Submit";

    /// <summary>FabrCore surface data type carried on adaptive-card render messages.</summary>
    public const string SurfaceAdaptiveCardDataType = "application/vnd.fabrcore.surface.adaptive-card+json";

    /// <summary>Attachment content type Microsoft channels require for Adaptive Cards.</summary>
    public const string AdaptiveCardContentType = "application/vnd.microsoft.card.adaptive";

    // Surface envelopes nest the card at most a few levels deep; the bound only guards
    // against pathological payloads.
    private const int MaxCardSearchDepth = 8;

    // Surface action events route to "app" (client-side handler) or "agent"; from this channel
    // the receiving agent is the only possible handler.
    private const string RouteToAgent = "agent";

    // Matches the serializer surface clients use for action events (camelCase properties,
    // verbatim payload keys).
    private static readonly JsonSerializerOptions ActionEventJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>True when the message is a FabrCore surface adaptive-card render.</summary>
    public static bool IsAdaptiveCardRender(AgentMessage message)
        => string.Equals(message.MessageType, UiRenderMessageType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.DataType, SurfaceAdaptiveCardDataType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the Adaptive Card from an adaptive-card render message into a channel
    /// attachment. Returns false when the message is not an adaptive-card render or no
    /// Adaptive Card can be parsed from its data.
    /// </summary>
    public static bool TryCreateAdaptiveCardAttachment(AgentMessage message, out Attachment? attachment)
    {
        attachment = null;

        if (!IsAdaptiveCardRender(message) || message.Data is not { Length: > 0 })
        {
            return false;
        }

        JsonElement card;
        try
        {
            using var document = JsonDocument.Parse(message.Data);
            if (!TryFindAdaptiveCard(document.RootElement, MaxCardSearchDepth, out var found))
            {
                return false;
            }

            card = found.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        // Content must stay a parsed object graph; serializing the card to a string here would
        // deliver a quoted JSON string the channel cannot render.
        attachment = new Attachment
        {
            ContentType = AdaptiveCardContentType,
            Content = card,
        };
        return true;
    }

    /// <summary>
    /// Composes the outgoing channel activity for a Copilot turn from the agent's direct reply
    /// and any surface renders the agent delivered to its principal during the turn. Cards are
    /// combined onto one activity, with the reply text (if any) as the accompanying text; with
    /// no cards the reply text is sent alone, using <paramref name="emptyResponseText"/> when
    /// even that is missing. Renders whose payload contains no parseable card are skipped and
    /// their ids returned via <paramref name="unmappedRenderIds"/> for the caller to log.
    /// </summary>
    public static IActivity BuildReplyActivity(
        AgentMessage? reply,
        IReadOnlyList<AgentMessage> surfaceRenders,
        string emptyResponseText,
        out List<string> unmappedRenderIds)
    {
        unmappedRenderIds = [];
        var attachments = new List<Attachment>();

        if (reply is not null && IsAdaptiveCardRender(reply))
        {
            if (TryCreateAdaptiveCardAttachment(reply, out var replyCard))
            {
                attachments.Add(replyCard!);
            }
            else
            {
                unmappedRenderIds.Add(reply.Id);
            }
        }

        foreach (var render in surfaceRenders)
        {
            if (render.Id == reply?.Id)
            {
                continue;
            }

            if (TryCreateAdaptiveCardAttachment(render, out var card))
            {
                attachments.Add(card!);
            }
            else
            {
                unmappedRenderIds.Add(render.Id);
            }
        }

        var text = reply?.Message;
        if (attachments.Count > 0)
        {
            var activity = MessageFactory.Attachment(attachments);
            if (!string.IsNullOrWhiteSpace(text))
            {
                activity.Text = text;
            }

            return activity;
        }

        return MessageFactory.Text(string.IsNullOrWhiteSpace(text) ? emptyResponseText : text);
    }

    /// <summary>
    /// Maps an Adaptive Card submit — the incoming activity's <c>Value</c>, holding the action's
    /// <c>data</c> merged with the card's input values — to the FabrCore surface
    /// <see cref="UiActionMessageType"/> message shape: <c>Data</c> carries a UTF-8 JSON action
    /// event (kind <c>ui.action</c>, actionType <see cref="SubmitActionType"/>, resolved
    /// <c>actionId</c>/<c>verb</c>, and the submit payload), and <c>Message</c> is the payload's
    /// <c>messageTemplate</c> with <c>{token}</c> placeholders expanded, when one is present.
    /// Returns false when the value is missing or is not a JSON object.
    /// </summary>
    public static bool TryCreateUiActionMessage(object? activityValue, [NotNullWhen(true)] out AgentMessage? message)
    {
        message = null;

        if (!TryGetSubmitPayload(activityValue, out var payload))
        {
            return false;
        }

        var text = ExpandMessageTemplate(payload);
        var actionEvent = new UiActionEvent
        {
            EnvelopeId = GetNonEmptyString(payload, "envelopeId"),
            ActionId = ResolveActionId(payload),
            Verb = GetNonEmptyString(payload, "verb"),
            Title = GetNonEmptyString(payload, "title"),
            Message = text,
            Payload = BuildEventPayload(payload),
        };

        message = new AgentMessage
        {
            MessageType = UiActionMessageType,
            DataType = SurfaceAdaptiveCardDataType,
            Data = JsonSerializer.SerializeToUtf8Bytes(actionEvent, ActionEventJsonOptions),
            Message = text,
            // Surface clients dispatch ui.action one-way; this channel needs the agent's
            // reply to show in the conversation, so the bridge sends a request instead.
            Kind = MessageKind.Request,
        };
        return true;
    }

    private static bool TryGetSubmitPayload(object? activityValue, out JsonElement payload)
    {
        payload = default;
        try
        {
            switch (activityValue)
            {
                case null:
                    return false;
                case JsonElement element:
                    payload = element;
                    break;
                case string json:
                    using (var document = JsonDocument.Parse(json))
                    {
                        payload = document.RootElement.Clone();
                    }
                    break;
                default:
                    payload = JsonSerializer.SerializeToElement(activityValue, ActionEventJsonOptions);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }

        return payload.ValueKind == JsonValueKind.Object;
    }

    // Mirrors the surface dispatcher's action-id resolution: explicit id keys win, then the
    // verb, then the button title, then the bare action type.
    private static string ResolveActionId(JsonElement payload)
        => GetNonEmptyString(payload, "actionId")
            ?? GetNonEmptyString(payload, "id")
            ?? GetNonEmptyString(payload, "verb")
            ?? GetNonEmptyString(payload, "title")
            ?? SubmitActionType;

    private static Dictionary<string, object?> BuildEventPayload(JsonElement payload)
    {
        // The channel already merged the action data with the card inputs, so flattening the
        // value's own properties yields the same payload surface clients build; the authored
        // keys win over the synthesized actionType, as they do in the surface dispatcher.
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["actionType"] = SubmitActionType,
        };

        foreach (var property in payload.EnumerateObject())
        {
            result[property.Name] = property.Value;
        }

        return result;
    }

    private static string? ExpandMessageTemplate(JsonElement payload)
    {
        var template = GetNonEmptyString(payload, "messageTemplate");
        if (template is null)
        {
            return null;
        }

        return TemplateTokenRegex().Replace(template, match =>
            TryGetPath(payload, match.Groups["name"].Value, out var value)
                ? StringifyToken(value)
                : match.Value);
    }

    private static string? GetNonEmptyString(JsonElement payload, string name)
    {
        if (!TryGetPropertyIgnoreCase(payload, name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPath(JsonElement payload, string path, out JsonElement value)
    {
        value = payload;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryGetPropertyIgnoreCase(value, part, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static string StringifyToken(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText(),
        };

    // Same token syntax surface clients use for action message templates.
    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9_.-]+)\}")]
    private static partial Regex TemplateTokenRegex();

    private static bool TryFindAdaptiveCard(JsonElement element, int depth, out JsonElement card)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "AdaptiveCard", StringComparison.Ordinal))
            {
                card = element;
                return true;
            }

            if (depth > 0)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindAdaptiveCard(property.Value, depth - 1, out card))
                    {
                        return true;
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array && depth > 0)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAdaptiveCard(item, depth - 1, out card))
                {
                    return true;
                }
            }
        }

        card = default;
        return false;
    }

    /// <summary>
    /// Wire shape of the surface action event carried in a ui.action message's <c>Data</c>,
    /// duplicated from FabrCore.Surface's <c>AdaptiveCardActionEvent</c> (this addon deliberately
    /// does not reference that project). Nullable members serialize as explicit nulls, matching
    /// what surface clients emit.
    /// </summary>
    private sealed class UiActionEvent
    {
        public string Version { get; set; } = "2.0";

        public string? EnvelopeId { get; set; }

        public string Kind { get; set; } = UiActionMessageType;

        public string ActionType { get; set; } = SubmitActionType;

        public string ActionId { get; set; } = SubmitActionType;

        public string? Verb { get; set; }

        public string? Title { get; set; }

        public string? Url { get; set; }

        public string RouteTo { get; set; } = RouteToAgent;

        public string? Message { get; set; }

        public string? TargetAgent { get; set; }

        public Dictionary<string, object?> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public object? Result { get; set; }
    }
}
