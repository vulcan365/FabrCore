using System.Text.Json;
using AdaptiveCards;
using FabrCore.Surface.Configuration;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Services;
using FabrCore.Surface.Templating;
using Microsoft.Extensions.Options;

namespace FabrCore.Surface.Validation;

public sealed class AdaptiveCardSurfaceValidator
{
    private readonly SurfaceOptions options;
    private readonly SurfaceDefinition? definition;

    public AdaptiveCardSurfaceValidator(IOptions<SurfaceOptions> options)
        : this(options.Value, null)
    {
    }

    public AdaptiveCardSurfaceValidator(SurfaceOptions options, SurfaceDefinition? definition = null)
    {
        this.options = options;
        this.definition = definition;
    }

    public AdaptiveCardSurfaceValidationResult Validate(AdaptiveCardSurfaceEnvelope? envelope)
    {
        var errors = new List<string>();
        var rejectedActionReasons = new List<string>();

        if (envelope == null)
        {
            errors.Add("Surface envelope is required.");
            return new AdaptiveCardSurfaceValidationResult(false, errors);
        }

        if (string.IsNullOrWhiteSpace(envelope.Version))
        {
            errors.Add("Surface envelope version is required.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Id))
        {
            errors.Add("Surface envelope id is required.");
        }

        if (envelope.Card.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            errors.Add("Adaptive Card template is required.");
            return new AdaptiveCardSurfaceValidationResult(false, errors);
        }

        ValidateJsonSize(envelope.Card, "Adaptive Card template", errors);
        if (envelope.Data is { } data)
        {
            ValidateJsonSize(data, "Adaptive Card data", errors);
        }

        var expandedCard = AdaptiveCardTemplateExpander.Expand(envelope.Card, envelope.Data);
        var actionCount = CountActions(expandedCard);
        ValidateCard(expandedCard, errors, rejectedActionReasons);

        return errors.Count == 0
            ? new AdaptiveCardSurfaceValidationResult(true, [], actionCount, actionCount, [])
            : new AdaptiveCardSurfaceValidationResult(
                false,
                errors,
                actionCount,
                Math.Max(0, actionCount - rejectedActionReasons.Count),
                rejectedActionReasons);
    }

    public AdaptiveCardSurfaceValidationResult ValidateExpandedCard(JsonElement expandedCard)
    {
        var errors = new List<string>();
        var rejectedActionReasons = new List<string>();
        var actionCount = CountActions(expandedCard);
        ValidateCard(expandedCard, errors, rejectedActionReasons);
        return errors.Count == 0
            ? new AdaptiveCardSurfaceValidationResult(true, [], actionCount, actionCount, [])
            : new AdaptiveCardSurfaceValidationResult(
                false,
                errors,
                actionCount,
                Math.Max(0, actionCount - rejectedActionReasons.Count),
                rejectedActionReasons);
    }

    private void ValidateCard(JsonElement card, List<string> errors, List<string> rejectedActionReasons)
    {
        if (card.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Adaptive Card must be a JSON object.");
            return;
        }

        if (!card.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "AdaptiveCard", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Adaptive Card type must be 'AdaptiveCard'.");
        }

        if (!card.TryGetProperty("version", out var versionProperty)
            || string.IsNullOrWhiteSpace(versionProperty.GetString()))
        {
            errors.Add("Adaptive Card version is required.");
        }
        else if (!IsVersionAllowed(versionProperty.GetString()!))
        {
            errors.Add($"Adaptive Card version '{versionProperty.GetString()}' exceeds the allowed version '{EffectiveMaxAdaptiveCardVersion}'.");
        }

        ValidateDepth(card, 0, errors);
        ValidateActionsAndUrls(card, errors, rejectedActionReasons);

        try
        {
            _ = AdaptiveCard.FromJson(card.GetRawText());
        }
        catch (Exception ex) when (ex is AdaptiveSerializationException or JsonException or ArgumentException)
        {
            errors.Add($"Adaptive Card parse failed: {ex.Message}");
        }
    }

    private void ValidateJsonSize(JsonElement value, string label, List<string> errors)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SurfaceJson.Options);
        if (bytes.Length > EffectiveMaxPayloadBytes)
        {
            errors.Add($"{label} exceeds the maximum size of {EffectiveMaxPayloadBytes} bytes.");
        }
    }

    private void ValidateDepth(JsonElement value, int depth, List<string> errors)
    {
        if (depth > EffectiveMaxDepth)
        {
            errors.Add($"Adaptive Card exceeds the maximum nesting depth of {EffectiveMaxDepth}.");
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                ValidateDepth(property.Value, depth + 1, errors);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateDepth(item, depth + 1, errors);
            }
        }
    }

    private void ValidateActionsAndUrls(JsonElement value, List<string> errors, List<string> rejectedActionReasons)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var typeProperty))
            {
                var type = typeProperty.GetString();
                if (!string.IsNullOrWhiteSpace(type) && type.StartsWith("Action.", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateAction(value, type, errors, rejectedActionReasons);
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                if (IsUrlProperty(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                {
                    ValidateUrl(property.Value.GetString(), property.Name, errors);
                }

                ValidateActionsAndUrls(property.Value, errors, rejectedActionReasons);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateActionsAndUrls(item, errors, rejectedActionReasons);
            }
        }
    }

    private void ValidateAction(JsonElement action, string actionType, List<string> errors, List<string> rejectedActionReasons)
    {
        if (!EffectiveAllowedActionTypes.Contains(actionType))
        {
            AddRejectedAction(errors, rejectedActionReasons, $"Adaptive Card action type '{actionType}' is not allowed.");
        }

        if (action.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
        {
            ValidateUrl(url.GetString(), "url", errors, rejectedActionReasons);
        }

        if (string.Equals(actionType, AdaptiveCardActionTypes.Execute, StringComparison.OrdinalIgnoreCase)
            && action.TryGetProperty("verb", out var verb)
            && verb.ValueKind == JsonValueKind.String
            && !IsActionVerbAllowed(verb.GetString()))
        {
            AddRejectedAction(errors, rejectedActionReasons, $"Adaptive Card action verb '{verb.GetString()}' is not allowed.");
        }

        if (TryGetFabrcoreData(action, out var data)
            && data.TryGetProperty("targetAgent", out var targetAgent)
            && targetAgent.ValueKind == JsonValueKind.String
            && !IsTargetAgentAllowed(targetAgent.GetString()))
        {
            AddRejectedAction(errors, rejectedActionReasons, $"Target agent '{targetAgent.GetString()}' is not allowed.");
        }
    }

    private void ValidateUrl(string? url, string label, List<string> errors, List<string>? rejectedActionReasons = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !EffectiveAllowHttpUrls)))
        {
            var message = $"Adaptive Card {label} '{url}' is not an allowed URL.";
            errors.Add(message);
            rejectedActionReasons?.Add(message);
        }
    }

    private static void AddRejectedAction(List<string> errors, List<string> rejectedActionReasons, string message)
    {
        errors.Add(message);
        rejectedActionReasons.Add(message);
    }

    private static int CountActions(JsonElement value)
    {
        var count = 0;
        CountActions(value, ref count);
        return count;
    }

    private static void CountActions(JsonElement value, ref int count)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var typeProperty)
                && typeProperty.ValueKind == JsonValueKind.String
                && typeProperty.GetString()?.StartsWith("Action.", StringComparison.OrdinalIgnoreCase) == true)
            {
                count++;
            }

            foreach (var property in value.EnumerateObject())
            {
                CountActions(property.Value, ref count);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CountActions(item, ref count);
            }
        }
    }

    private bool IsActionVerbAllowed(string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb) || EffectiveAllowAnyActionVerb)
        {
            return true;
        }

        return EffectiveAllowedActionVerbs.Contains(verb);
    }

    private bool IsTargetAgentAllowed(string? targetAgent)
    {
        if (string.IsNullOrWhiteSpace(targetAgent) || EffectiveAllowUnknownTargetAgents)
        {
            return true;
        }

        return EffectiveAllowedTargetAgents.Contains(targetAgent);
    }

    private static bool TryGetFabrcoreData(JsonElement action, out JsonElement data)
    {
        data = default;
        if (!action.TryGetProperty("data", out var actionData) || actionData.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (actionData.TryGetProperty("fabrcore", out data) && data.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        data = actionData;
        return true;
    }

    private static bool IsUrlProperty(string propertyName)
        => string.Equals(propertyName, "url", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "iconUrl", StringComparison.OrdinalIgnoreCase);

    private string EffectiveMaxAdaptiveCardVersion
        => definition?.MaxAdaptiveCardVersion ?? options.MaxAdaptiveCardVersion;

    private int EffectiveMaxPayloadBytes
        => definition?.MaxPayloadBytes ?? options.MaxPayloadBytes;

    private int EffectiveMaxDepth
        => definition?.MaxDepth ?? options.MaxDepth;

    private bool EffectiveAllowHttpUrls
        => definition?.AllowHttpUrls ?? options.AllowHttpUrls;

    private bool EffectiveAllowAnyActionVerb
        => definition?.AllowAnyActionVerb ?? options.AllowAnyActionVerb;

    private bool EffectiveAllowUnknownTargetAgents
        => definition?.AllowUnknownTargetAgents ?? options.AllowUnknownTargetAgents;

    private HashSet<string> EffectiveAllowedActionTypes
        => BuildSet(options.AllowedActionTypes, definition?.AllowedActionTypes);

    private HashSet<string> EffectiveAllowedActionVerbs
        => BuildSet(options.AllowedActionVerbs, definition?.AllowedActionVerbs);

    private HashSet<string> EffectiveAllowedTargetAgents
        => BuildSet(options.AllowedTargetAgents, definition?.AllowedTargetAgents);

    private static HashSet<string> BuildSet(IEnumerable<string> optionsValues, IEnumerable<string>? definitionValues)
    {
        var values = new HashSet<string>(optionsValues, StringComparer.OrdinalIgnoreCase);
        if (definitionValues is not null)
        {
            foreach (var value in definitionValues)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private bool IsVersionAllowed(string version)
        => Version.TryParse(version, out var actual)
           && Version.TryParse(EffectiveMaxAdaptiveCardVersion, out var max)
           && actual <= max;
}
