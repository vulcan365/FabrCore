using FabrCore.Core;

namespace FabrCore.Surface;

public static class SurfaceEventStreamSubscriptions
{
    public static List<EventStreamSubscription> Split(string value)
        => SplitTokens(value)
            .Select(Parse)
            .Distinct()
            .ToList();

    public static string Format(IEnumerable<EventStreamSubscription> subscriptions)
        => string.Join(Environment.NewLine, subscriptions.Select(Format));

    public static EventStreamSubscription Parse(string value)
    {
        var trimmed = Required(value, "Stream");
        var separator = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (separator < 0)
        {
            separator = trimmed.IndexOf('.', StringComparison.Ordinal);
        }

        if (separator < 0)
        {
            return EventStreamSubscription.For(trimmed, trimmed);
        }

        var ns = trimmed[..separator].Trim();
        var channel = trimmed[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(channel))
        {
            throw new InvalidOperationException("Streams must be formatted as namespace.channel.");
        }

        return EventStreamSubscription.For(ns, channel);
    }

    private static string Format(EventStreamSubscription subscription)
    {
        if (string.Equals(subscription.Namespace, subscription.Channel, StringComparison.OrdinalIgnoreCase))
        {
            return subscription.Namespace;
        }

        return subscription.ToString();
    }

    private static IEnumerable<string> SplitTokens(string value)
        => value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Required(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return value.Trim();
    }
}
