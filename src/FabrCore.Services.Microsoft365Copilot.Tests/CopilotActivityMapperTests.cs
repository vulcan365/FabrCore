using System.Text;
using System.Text.Json;
using FabrCore.Core;
using Microsoft.Agents.Core.Models;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotActivityMapperTests
{
    private const string EmptyResponseText = "The agent returned an empty response.";

    private const string Card = """
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [ { "type": "TextBlock", "text": "Hello from my agent", "wrap": true } ]
        }
        """;

    private static AgentMessage UiRenderMessage(string json, string? text = null) => new()
    {
        MessageType = CopilotActivityMapper.UiRenderMessageType,
        DataType = CopilotActivityMapper.SurfaceAdaptiveCardDataType,
        Data = Encoding.UTF8.GetBytes(json),
        Message = text,
        Kind = MessageKind.Response,
    };

    [TestMethod]
    public void BareCard_BecomesAdaptiveCardAttachment()
    {
        Assert.IsTrue(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(
            UiRenderMessage(Card), out var attachment));

        Assert.AreEqual(CopilotActivityMapper.AdaptiveCardContentType, attachment!.ContentType);

        // Content must be a parsed JSON object, not a re-serialized string — a string here is
        // exactly the double-encoding Copilot cannot render.
        Assert.IsInstanceOfType<JsonElement>(attachment.Content);
        var card = (JsonElement)attachment.Content!;
        Assert.AreEqual(JsonValueKind.Object, card.ValueKind);
        Assert.AreEqual("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.AreEqual("1.5", card.GetProperty("version").GetString());
    }

    [TestMethod]
    public void EnvelopeWrappedCard_IsUnwrapped()
    {
        var envelope = $$"""
            {
              "surfaceId": "main",
              "revision": 3,
              "payload": { "kind": "adaptive-card", "card": {{Card}} }
            }
            """;

        Assert.IsTrue(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(
            UiRenderMessage(envelope), out var attachment));

        var card = (JsonElement)attachment!.Content!;
        Assert.AreEqual("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.AreEqual("Hello from my agent",
            card.GetProperty("body")[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public void NonUiRenderMessage_IsNotMapped()
    {
        var message = new AgentMessage { MessageType = "chat", Message = "plain text" };

        Assert.IsFalse(CopilotActivityMapper.IsAdaptiveCardRender(message));
        Assert.IsFalse(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(message, out var attachment));
        Assert.IsNull(attachment);
    }

    [TestMethod]
    public void UiRenderWithDifferentDataType_IsNotMapped()
    {
        var message = UiRenderMessage(Card);
        message.DataType = "application/vnd.fabrcore.surface.html+json";

        Assert.IsFalse(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(message, out _));
    }

    [TestMethod]
    public void InvalidJsonPayload_IsNotMapped()
    {
        Assert.IsFalse(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(
            UiRenderMessage("{ not json"), out _));
    }

    [TestMethod]
    public void PayloadWithoutACard_IsNotMapped()
    {
        Assert.IsFalse(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(
            UiRenderMessage("""{ "payload": { "kind": "something-else" } }"""), out _));
    }

    [TestMethod]
    public void EmptyData_IsNotMapped()
    {
        var message = UiRenderMessage(Card);
        message.Data = null;

        Assert.IsFalse(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(message, out _));
    }

    [TestMethod]
    public void AttachmentSerializes_AsJsonObjectNotString()
    {
        Assert.IsTrue(CopilotActivityMapper.TryCreateAdaptiveCardAttachment(
            UiRenderMessage(Card), out var attachment));

        var json = JsonSerializer.Serialize(attachment);
        using var document = JsonDocument.Parse(json);
        var content = document.RootElement.GetProperty("Content");

        Assert.AreEqual(JsonValueKind.Object, content.ValueKind,
            "Attachment content serialized as a quoted string — the card was double-encoded.");
        Assert.AreEqual("AdaptiveCard", content.GetProperty("type").GetString());
    }

    // ── BuildReplyActivity composition ──

    [TestMethod]
    public void TextReplyWithoutRenders_IsPlainText()
    {
        var reply = new AgentMessage { MessageType = "chat", Message = "just words" };

        var activity = CopilotActivityMapper.BuildReplyActivity(reply, [], EmptyResponseText, out var unmapped);

        Assert.AreEqual("just words", activity.Text);
        Assert.IsTrue(activity.Attachments is null or { Count: 0 });
        Assert.AreEqual(0, unmapped.Count);
    }

    [TestMethod]
    public void NullReplyWithoutRenders_FallsBackToEmptyResponseText()
    {
        var activity = CopilotActivityMapper.BuildReplyActivity(null, [], EmptyResponseText, out _);

        Assert.AreEqual(EmptyResponseText, activity.Text);
    }

    [TestMethod]
    public void CardReply_BecomesAttachmentWithAccompanyingText()
    {
        var reply = UiRenderMessage(Card, text: "Here is your report.");

        var activity = CopilotActivityMapper.BuildReplyActivity(reply, [], EmptyResponseText, out _);

        Assert.AreEqual(ActivityTypes.Message, activity.Type);
        Assert.AreEqual(1, activity.Attachments.Count);
        Assert.AreEqual("Here is your report.", activity.Text);
    }

    [TestMethod]
    public void CapturedRenders_AreAttachedAlongsideTextReply()
    {
        var reply = new AgentMessage { MessageType = "chat", Message = "See the cards below." };
        var renders = new[] { UiRenderMessage(Card), UiRenderMessage(Card) };

        var activity = CopilotActivityMapper.BuildReplyActivity(reply, renders, EmptyResponseText, out var unmapped);

        Assert.AreEqual(2, activity.Attachments.Count);
        Assert.AreEqual("See the cards below.", activity.Text);
        Assert.AreEqual(0, unmapped.Count);
    }

    [TestMethod]
    public void CapturedRendersWithEmptyReply_SendCardsWithoutFallbackText()
    {
        var activity = CopilotActivityMapper.BuildReplyActivity(
            null, [UiRenderMessage(Card)], EmptyResponseText, out _);

        Assert.AreEqual(1, activity.Attachments.Count);
        Assert.AreNotEqual(EmptyResponseText, activity.Text);
    }

    [TestMethod]
    public void RenderMatchingReplyId_IsNotDuplicated()
    {
        var reply = UiRenderMessage(Card);

        var activity = CopilotActivityMapper.BuildReplyActivity(
            reply, [reply, UiRenderMessage(Card)], EmptyResponseText, out _);

        Assert.AreEqual(2, activity.Attachments.Count);
    }

    [TestMethod]
    public void UnparseableRender_IsSkippedAndReported()
    {
        var broken = UiRenderMessage("{ not json");

        var activity = CopilotActivityMapper.BuildReplyActivity(
            null, [broken, UiRenderMessage(Card)], EmptyResponseText, out var unmapped);

        Assert.AreEqual(1, activity.Attachments.Count);
        Assert.AreEqual(1, unmapped.Count);
        Assert.AreEqual(broken.Id, unmapped[0]);
    }
}
