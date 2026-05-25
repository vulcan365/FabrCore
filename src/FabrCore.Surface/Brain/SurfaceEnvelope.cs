using System.Text.Json;
using System.Text.RegularExpressions;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Brain;

/// <summary>
/// Defensive parser for Adaptive Card Surface envelopes produced by LLM planning calls.
/// </summary>
public static class SurfaceEnvelope
{
    public const string FenceName = "fabrcore-adaptive-card-surface";

    private static readonly Regex EnvelopeBlockRegex = new(
        @"```fabrcore-adaptive-card-surface\s*\r?\n(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static AdaptiveCardSurfaceEnvelope? TryExtractEnvelope(string? responseText)
    {
        var element = TryExtract(responseText);
        if (element is null)
        {
            return null;
        }

        try
        {
            return element.Value.Deserialize<AdaptiveCardSurfaceEnvelope>(SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? TryExtract(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var matches = EnvelopeBlockRegex.Matches(responseText);
        if (matches.Count > 0)
        {
            var body = matches[^1].Groups["body"].Value.Trim();
            return ParseOrNull(body);
        }

        var trimmed = responseText.Trim();
        var lastClose = trimmed.LastIndexOf('}');
        if (lastClose < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = lastClose; i >= 0; i--)
        {
            var ch = trimmed[i];
            if (ch == '}')
            {
                depth++;
            }
            else if (ch == '{')
            {
                depth--;
                if (depth == 0)
                {
                    return ParseOrNull(trimmed.Substring(i, lastClose - i + 1));
                }
            }
        }

        return null;
    }

    public static string StripEnvelopeBlocks(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var cleaned = EnvelopeBlockRegex.Replace(text, string.Empty);
        var openFence = cleaned.IndexOf("```fabrcore-adaptive-card-surface", StringComparison.OrdinalIgnoreCase);
        if (openFence >= 0)
        {
            cleaned = cleaned[..openFence];
        }

        return cleaned.Trim();
    }

    private static JsonElement? ParseOrNull(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
