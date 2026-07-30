using System.Text.Json;
using FabrCore.Core;
using FabrCore.Services.Memory.Services;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class ToolResultCompressorTests
{
    [TestMethod]
    public void CompressToolResults_CompressesOnlyLargeOldToolMessages()
    {
        var largeOutput = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"row {i}: tool data"));
        var messages = new List<StoredChatMessage>
        {
            Message("tool", largeOutput, "sql-query"),
            Message("assistant", new string('a', 5000)),
            Message("tool", new string('b', 5000)),
            Message("user", "latest question")
        };

        var (result, compressed) = ToolResultCompressor.CompressToolResults(
            messages, keepLastN: 2, thresholdChars: 500, keepHeadChars: 100, keepTailChars: 100);

        Assert.AreEqual(1, compressed);
        StringAssert.Contains(result[0].ContentsJson, "chars omitted");
        Assert.AreEqual(messages[0].Id, result[0].Id);
        Assert.AreEqual("sql-query", result[0].AuthorName);
        Assert.AreEqual(messages[1].ContentsJson, result[1].ContentsJson,
            "Non-tool messages must never be compressed.");
        Assert.AreEqual(messages[2].ContentsJson, result[2].ContentsJson,
            "Messages in the keep window must remain intact.");
    }

    [TestMethod]
    public void CompressToolResults_DoesNotMutateInput()
    {
        var messages = new List<StoredChatMessage>
        {
            Message("tool", new string('z', 5000)),
            Message("user", "question")
        };
        var original = messages[0].ContentsJson;

        _ = ToolResultCompressor.CompressToolResults(
            messages, keepLastN: 1, thresholdChars: 500, keepHeadChars: 100, keepTailChars: 100);

        Assert.AreEqual(original, messages[0].ContentsJson);
    }

    private static StoredChatMessage Message(string role, string text, string? author = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Role = role,
        AuthorName = author,
        Timestamp = DateTime.UtcNow,
        ContentsJson = JsonSerializer.Serialize(
            new List<AIContent> { new TextContent(text) },
            Microsoft.Agents.AI.AgentAbstractionsJsonUtilities.DefaultOptions)
    };
}
