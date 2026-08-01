using System.Text.RegularExpressions;
using Markdig;

namespace FabrCore.Surface.CommandCenter;

public static partial class SurfaceMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static string Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        return SanitizeGeneratedHtml(html);
    }

    private static string SanitizeGeneratedHtml(string html)
    {
        html = ReplaceUnsafeAttributes(UnsafeHref(), html, " href=\"#\"");
        html = ReplaceUnsafeAttributes(UnsafeSrc(), html, " src=\"\"");
        html = ImageTags().Replace(html, string.Empty);
        return html;
    }

    private static bool IsSafeUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith('#') || value.StartsWith('/'))
        {
            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" or "mailto";
    }

    [GeneratedRegex(@"\shref=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeHref();

    [GeneratedRegex(@"\ssrc=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeSrc();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTags();

    private static string ReplaceUnsafeAttributes(Regex regex, string input, string replacement)
        => regex.Replace(input, match =>
        {
            var value = match.Groups[1].Value;
            return IsSafeUri(value) ? match.Value : replacement;
        });
}
