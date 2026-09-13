using System.Text.Json;
using FabrCore.Core;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk;

/// <summary>Storage-neutral transcript operations shared by durable and request compaction.</summary>
internal static class CompactionTranscript
{
    internal static List<AIContent> Contents(StoredChatMessage message) =>
        JsonSerializer.Deserialize<List<AIContent>>(message.ContentsJson, ChatMessageSerializerOptions.Instance)
        ?? throw new InvalidOperationException("Compaction encountered an invalid transcript.");

    internal static bool IsInstruction(StoredChatMessage message) =>
        message.AuthorName != "compaction" && (message.Role == "system" || message.Role == "developer");

    // Do not split parallel tool calls from their results, including mixed-content messages.
    internal static List<List<StoredChatMessage>> Groups(IEnumerable<StoredChatMessage> messages)
    {
        List<List<StoredChatMessage>> groups = [];
        List<StoredChatMessage> current = [];
        HashSet<string> pending = [];
        foreach (var message in messages)
        {
            current.Add(message);
            foreach (var content in Contents(message))
            {
                if (content is FunctionCallContent call && !pending.Add(call.CallId))
                    throw new InvalidOperationException("Duplicate tool call in compaction transcript.");
                if (content is FunctionResultContent result && !pending.Remove(result.CallId))
                    throw new InvalidOperationException("Unpaired tool result in compaction transcript.");
            }
            if (pending.Count == 0)
            {
                groups.Add(current);
                current = [];
            }
        }
        // An unfinished call must remain intact; it is never valid summarization input.
        if (pending.Count > 0)
            throw new InvalidOperationException("Cannot compact a transcript with unfinished tool calls.");
        return groups;
    }

    internal static string Format(StoredChatMessage message) =>
        $"[{message.Role}; author={message.AuthorName}; id={message.Id}] {message.ContentsJson}";

    internal static string ResultText(object? result) => result is string text ? text
        : JsonSerializer.Serialize(result, ChatMessageSerializerOptions.Instance);

    internal static string Bound(string text, int maxChars)
    {
        const string marker = "\n[tool output excerpt; middle omitted]\n";
        if (text.Length <= maxChars) return text;
        if (maxChars <= marker.Length) return "[omitted]";
        var remaining = maxChars - marker.Length;
        return text[..((remaining + 1) / 2)] + marker + text[^(remaining / 2)..];
    }
}
