using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FabrCore.Services.GraphRag.Services;

internal static partial class EmailSourceDocumentParser
{
    private static readonly HashSet<string> EmailHeaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "messageId",
        "internetMessageId",
        "conversationId",
        "from",
        "to",
        "cc",
        "bcc",
        "receivedDateTimeUtc",
        "sentDateTimeUtc",
        "lastModifiedDateTimeUtc",
        "webLink",
        "hasAttachments",
        "importance",
        "isRead"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    internal static IngestSourceDocument Normalize(string fileName, string markdownContent)
    {
        if (!TryParseEmail(fileName, markdownContent, out var email))
        {
            return new IngestSourceDocument(
                FileName: fileName,
                SourceKind: "Markdown",
                SourceKey: fileName,
                SourceTitle: fileName,
                SourceOccurredAtUtc: null,
                MetadataJson: null,
                ContentForIngestion: markdownContent,
                ExtractionContext: null);
        }

        return email;
    }

    internal static bool TryParseEmail(
        string fileName,
        string markdownContent,
        out IngestSourceDocument source)
    {
        source = default!;
        if (!TryReadFrontMatter(markdownContent, out var fields, out var bodyStart))
            return false;

        if (!fields.Keys.Any(EmailHeaderKeys.Contains))
            return false;

        var subject = Get(fields, "subject");
        var sourceKey = NormalizeSourceKey(FirstNonEmpty(
            Get(fields, "internetMessageId"),
            Get(fields, "messageId"),
            fileName)!);

        var occurredAt = ParseUtcDateTime(Get(fields, "receivedDateTimeUtc"))
            ?? ParseUtcDateTime(Get(fields, "sentDateTimeUtc"));

        var body = RemoveEmailMetadataSections(markdownContent[bodyStart..]).Trim();
        if (string.IsNullOrWhiteSpace(body))
            body = markdownContent[bodyStart..].Trim();

        var metadata = BuildEmailMetadata(fields);
        var metadataJson = JsonSerializer.Serialize(metadata, JsonOptions);
        var title = FirstNonEmpty(subject, fileName)!;

        source = new IngestSourceDocument(
            FileName: fileName,
            SourceKind: "Email",
            SourceKey: sourceKey,
            SourceTitle: title,
            SourceOccurredAtUtc: occurredAt,
            MetadataJson: metadataJson,
            ContentForIngestion: body,
            ExtractionContext: BuildExtractionContext(metadata));

        return true;
    }

    private static bool TryReadFrontMatter(
        string markdownContent,
        out Dictionary<string, string> fields,
        out int bodyStart)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bodyStart = 0;

        var normalized = markdownContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)
            && !string.Equals(normalized.TrimEnd(), "---", StringComparison.Ordinal))
        {
            return false;
        }

        var closing = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (closing < 0)
            return false;

        var afterClosing = closing + "\n---".Length;
        if (afterClosing < normalized.Length && normalized[afterClosing] == '\n')
            afterClosing++;

        bodyStart = ToOriginalIndex(markdownContent, normalized, afterClosing);
        var header = normalized[4..closing];
        foreach (var rawLine in header.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim();
            var value = UnquoteYamlScalar(line[(colon + 1)..].Trim());
            if (!string.IsNullOrWhiteSpace(key))
                fields[key] = value;
        }

        return fields.Count > 0;
    }

    private static int ToOriginalIndex(string original, string normalized, int normalizedIndex)
    {
        if (original.Length == normalized.Length)
            return normalizedIndex;

        var originalIndex = 0;
        var currentNormalizedIndex = 0;
        while (originalIndex < original.Length && currentNormalizedIndex < normalizedIndex)
        {
            if (original[originalIndex] == '\r'
                && originalIndex + 1 < original.Length
                && original[originalIndex + 1] == '\n')
            {
                originalIndex += 2;
                currentNormalizedIndex++;
            }
            else
            {
                originalIndex++;
                currentNormalizedIndex++;
            }
        }

        return originalIndex;
    }

    private static string RemoveEmailMetadataSections(string body)
    {
        var withoutMetadata = EmailMetadataSectionRegex().Replace(body, string.Empty);
        var bodyMatch = BodyHeadingRegex().Match(withoutMetadata);
        if (bodyMatch.Success)
            return withoutMetadata[(bodyMatch.Index + bodyMatch.Length)..];

        var titleMatch = MarkdownTitleRegex().Match(withoutMetadata);
        if (titleMatch.Success && titleMatch.Index == 0)
            return withoutMetadata[(titleMatch.Index + titleMatch.Length)..];

        return withoutMetadata;
    }

    private static Dictionary<string, object?> BuildEmailMetadata(Dictionary<string, string> fields)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceKind"] = "Email"
        };

        foreach (var (key, value) in fields.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (IsDateField(key) && ParseUtcDateTime(value) is DateTime dt)
                metadata[key] = dt.ToString("O", CultureInfo.InvariantCulture);
            else if (IsBooleanField(key) && bool.TryParse(value, out var b))
                metadata[key] = b;
            else
                metadata[key] = value;
        }

        return metadata;
    }

    private static string BuildExtractionContext(Dictionary<string, object?> metadata)
    {
        static string? StringValue(Dictionary<string, object?> values, string key) =>
            values.TryGetValue(key, out var value) ? value?.ToString() : null;

        var lines = new List<string>
        {
            "Email metadata context:",
            $"- Subject: {StringValue(metadata, "subject") ?? "(none)"}",
            $"- From: {StringValue(metadata, "from") ?? "(none)"}",
            $"- To: {StringValue(metadata, "to") ?? "(none)"}"
        };

        if (!string.IsNullOrWhiteSpace(StringValue(metadata, "cc")))
            lines.Add($"- Cc: {StringValue(metadata, "cc")}");
        if (!string.IsNullOrWhiteSpace(StringValue(metadata, "receivedDateTimeUtc")))
            lines.Add($"- Received: {StringValue(metadata, "receivedDateTimeUtc")}");
        if (!string.IsNullOrWhiteSpace(StringValue(metadata, "conversationId")))
            lines.Add($"- Conversation Id: {StringValue(metadata, "conversationId")}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string? Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string NormalizeSourceKey(string value)
    {
        const int maxLength = 500;
        if (value.Length <= maxLength)
            return value;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
        return $"{value[..(maxLength - hash.Length - 1)]}#{hash}";
    }

    private static DateTime? ParseUtcDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed.UtcDateTime;
    }

    private static bool IsDateField(string key) =>
        key.EndsWith("DateTimeUtc", StringComparison.OrdinalIgnoreCase);

    private static bool IsBooleanField(string key) =>
        string.Equals(key, "hasAttachments", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "isRead", StringComparison.OrdinalIgnoreCase);

    private static string UnquoteYamlScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        return value;
    }

    [GeneratedRegex(@"(?ims)^\s*##\s+Email Metadata\s*$.*?(?=^\s*##\s+|\z)")]
    private static partial Regex EmailMetadataSectionRegex();

    [GeneratedRegex(@"(?im)^\s*##\s+Body\s*$\s*")]
    private static partial Regex BodyHeadingRegex();

    [GeneratedRegex(@"(?im)^\s*#\s+.+\s*$\s*")]
    private static partial Regex MarkdownTitleRegex();
}

internal sealed record IngestSourceDocument(
    string FileName,
    string SourceKind,
    string SourceKey,
    string SourceTitle,
    DateTime? SourceOccurredAtUtc,
    string? MetadataJson,
    string ContentForIngestion,
    string? ExtractionContext)
{
    public string EntityName => SourceKind == "Email"
        ? Truncate($"{SourceTitle} ({SourceKey})", 500)
        : FileName;

    public string Description => SourceKind == "Email"
        ? $"Email: {SourceTitle}"
        : $"Document: {FileName}";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
