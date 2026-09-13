using System.Text.Json;
using FabrCore.Core;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Tier 1 compaction: compress large tool results in older messages without any LLM call.
/// Preserves tool name and head/tail of output. Messages inside the keep window are untouched.
/// </summary>
internal static class ToolResultCompressor
{
    /// <summary>
    /// Scan messages and compress tool results that exceed the threshold.
    /// Returns a new list (input is not mutated) and the count of messages compressed.
    /// </summary>
    /// <param name="messages">The full stored message list.</param>
    /// <param name="keepLastN">Number of recent messages to skip (live window).</param>
    /// <param name="thresholdChars">Compress tool results with ContentsJson longer than this.</param>
    /// <param name="keepHeadChars">Chars to preserve from the start of the tool output.</param>
    /// <param name="keepTailChars">Chars to preserve from the end of the tool output.</param>
    public static (List<StoredChatMessage> Messages, int Compressed) CompressToolResults(
        List<StoredChatMessage> messages,
        int keepLastN,
        int thresholdChars,
        int keepHeadChars,
        int keepTailChars)
    {
        if (messages.Count == 0)
            return (new List<StoredChatMessage>(messages), 0);

        var keepBoundary = Math.Max(0, messages.Count - keepLastN);
        var result = new List<StoredChatMessage>(messages.Count);
        var compressed = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];

            // Skip messages in the keep window
            if (i >= keepBoundary)
            {
                result.Add(msg);
                continue;
            }

            // Only compress tool role messages with large payloads
            if (!string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(msg.ContentsJson) ||
                msg.ContentsJson.Length <= thresholdChars)
            {
                result.Add(msg);
                continue;
            }

            // Extract readable text from the tool result
            var textContent = ExtractTextContent(msg.ContentsJson);
            if (textContent is null || textContent.Length <= thresholdChars)
            {
                result.Add(msg);
                continue;
            }

            // Build compressed placeholder
            var placeholder = BuildPlaceholder(textContent, msg.AuthorName, keepHeadChars, keepTailChars);

            // Create replacement message with same metadata but compressed content
            var compressedMsg = new StoredChatMessage
            {
                Id = msg.Id,
                Role = msg.Role,
                AuthorName = msg.AuthorName,
                Timestamp = msg.Timestamp,
                ContentsJson = SerializeTextContent(placeholder)
            };

            result.Add(compressedMsg);
            compressed++;
        }

        return (result, compressed);
    }

    private static string BuildPlaceholder(string fullText, string? toolName, int keepHead, int keepTail)
    {
        var originalChars = fullText.Length;
        var name = toolName ?? "tool";

        // Head: first N chars, break at newline if possible
        var head = fullText[..Math.Min(keepHead, fullText.Length)];
        var headBreak = head.LastIndexOf('\n');
        if (headBreak > keepHead / 2)
            head = head[..headBreak];

        // Tail: last N chars, break at newline if possible
        var tail = "";
        if (keepTail > 0 && fullText.Length > keepHead + keepTail)
        {
            tail = fullText[^Math.Min(keepTail, fullText.Length)..];
            var tailBreak = tail.IndexOf('\n');
            if (tailBreak > 0 && tailBreak < keepTail / 2)
                tail = tail[(tailBreak + 1)..];
        }

        var omittedChars = originalChars - head.Length - tail.Length;

        if (string.IsNullOrWhiteSpace(tail))
        {
            return $"{head}\n\n[{name}: {omittedChars:N0} chars omitted from {originalChars:N0} char result]";
        }

        return $"{head}\n\n[{name}: {omittedChars:N0} chars omitted from {originalChars:N0} char result]\n\n{tail}";
    }

    private static string? ExtractTextContent(string contentsJson)
    {
        try
        {
            var contents = JsonSerializer.Deserialize<List<AIContent>>(
                contentsJson, Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions);

            if (contents is null)
                return null;

            var texts = contents.OfType<TextContent>().Select(tc => tc.Text).Where(t => t is not null);
            var joined = string.Join("\n", texts);
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        catch
        {
            // If we can't parse the JSON, treat the raw JSON as the text for size checking
            return contentsJson;
        }
    }

    private static string SerializeTextContent(string text)
    {
        return JsonSerializer.Serialize(
            new List<AIContent> { new TextContent(text) },
            Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions);
    }
}
