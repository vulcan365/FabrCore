using System.Text.Json;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Builders;

public static class SurfaceActions
{
    public static object ToAgent(
        string title,
        string verb,
        string targetAgent,
        object? payload = null,
        string? messageTemplate = null)
        => RoutedAction(title, verb, SurfaceActionRoute.Agent, targetAgent, payload, messageTemplate);

    public static object ToApp(
        string title,
        string verb,
        object? payload = null,
        string? messageTemplate = null)
        => RoutedAction(title, verb, SurfaceActionRoute.App, null, payload, messageTemplate);

    public static object ToBoth(
        string title,
        string verb,
        string targetAgent,
        object? payload = null,
        string? messageTemplate = null)
        => RoutedAction(title, verb, SurfaceActionRoute.Both, targetAgent, payload, messageTemplate);

    public static object OpenUrl(string title, string url)
        => new Dictionary<string, object?>
        {
            ["type"] = AdaptiveCardActionTypes.OpenUrl,
            ["title"] = title,
            ["url"] = url
        };

    public static Dictionary<string, object?> RoutedActionData(
        string verb,
        string routeTo,
        string? targetAgent = null,
        object? payload = null,
        string? messageTemplate = null)
    {
        var data = ToDictionary(payload);
        data[SurfaceActionDataKeys.ActionId] = verb;
        data[SurfaceActionDataKeys.RouteTo] = routeTo;

        if (!string.IsNullOrWhiteSpace(targetAgent))
        {
            data[SurfaceActionDataKeys.TargetAgent] = targetAgent;
        }

        if (!string.IsNullOrWhiteSpace(messageTemplate))
        {
            data[SurfaceActionDataKeys.MessageTemplate] = messageTemplate;
        }

        return data;
    }

    private static object RoutedAction(
        string title,
        string verb,
        string routeTo,
        string? targetAgent,
        object? payload,
        string? messageTemplate)
        => new Dictionary<string, object?>
        {
            ["type"] = AdaptiveCardActionTypes.Execute,
            ["title"] = title,
            ["verb"] = verb,
            ["data"] = RoutedActionData(verb, routeTo, targetAgent, payload, messageTemplate)
        };

    private static Dictionary<string, object?> ToDictionary(object? payload)
    {
        if (payload is null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        if (payload is Dictionary<string, object?> dictionary)
        {
            return new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
        }

        if (payload is IReadOnlyDictionary<string, object?> readOnly)
        {
            return new Dictionary<string, object?>(readOnly, StringComparer.OrdinalIgnoreCase);
        }

        var element = JsonSerializer.SerializeToElement(payload, SurfaceJson.Options);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["value"] = JsonElementToObject(element)
            };
        }

        return element.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.OrdinalIgnoreCase);
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
}
