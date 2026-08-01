namespace FabrCore.Services.GraphRag.Services;

/// <summary>Converts an uploaded document stream to Markdown.</summary>
public interface IMarkdownConversionService
{
    Task<string> ConvertAsync(
        Stream source,
        string fileName,
        string? contentType = null,
        CancellationToken ct = default);
}
