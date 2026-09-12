using System.Text;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// Default OSS converter for Markdown and plain-text sources. Applications that
/// ingest binary documents can replace this registration with their own
/// <see cref="IMarkdownConversionService"/>.
/// </summary>
public sealed class PassThroughMarkdownConversionService : IMarkdownConversionService
{
    public async Task<string> ConvertAsync(
        Stream source,
        string fileName,
        string? contentType = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var extension = Path.GetExtension(fileName);
        var isText = extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(contentType, "application/markdown", StringComparison.OrdinalIgnoreCase);

        if (!isText)
        {
            throw new NotSupportedException(
                $"The default GraphRAG converter accepts Markdown or plain text only. " +
                $"Register a custom {nameof(IMarkdownConversionService)} to ingest '{extension}' files.");
        }

        using var reader = new StreamReader(
            source,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        var markdown = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new InvalidOperationException("The source document contained no text.");
        }

        return markdown;
    }
}
