using System.Text.Json;
using FabrCore.Core;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotUiActionMapperTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static JsonElement ActionEvent(AgentMessage message)
    {
        Assert.IsNotNull(message.Data);
        return JsonSerializer.Deserialize<JsonElement>(message.Data);
    }

    [TestMethod]
    public void SubmitPayload_BecomesUiActionRequest()
    {
        var value = Json("""{ "verb": "approve", "requestId": "PO-1001", "comment": "looks good" }""");

        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(value, out var message));

        Assert.AreEqual(CopilotActivityMapper.UiActionMessageType, message!.MessageType);
        Assert.AreEqual(CopilotActivityMapper.SurfaceAdaptiveCardDataType, message.DataType);
        Assert.AreEqual(MessageKind.Request, message.Kind);
        Assert.IsNull(message.Message);

        var actionEvent = ActionEvent(message);
        Assert.AreEqual("2.0", actionEvent.GetProperty("version").GetString());
        Assert.AreEqual("ui.action", actionEvent.GetProperty("kind").GetString());
        Assert.AreEqual("Action.Submit", actionEvent.GetProperty("actionType").GetString());
        Assert.AreEqual("agent", actionEvent.GetProperty("routeTo").GetString());
        Assert.AreEqual("approve", actionEvent.GetProperty("verb").GetString());

        // The submit payload is flattened into the event payload alongside the action type,
        // with the authored key casing preserved.
        var payload = actionEvent.GetProperty("payload");
        Assert.AreEqual("Action.Submit", payload.GetProperty("actionType").GetString());
        Assert.AreEqual("approve", payload.GetProperty("verb").GetString());
        Assert.AreEqual("PO-1001", payload.GetProperty("requestId").GetString());
        Assert.AreEqual("looks good", payload.GetProperty("comment").GetString());
    }

    [TestMethod]
    public void ActionId_PrefersExplicitIdKeys()
    {
        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(
            Json("""{ "actionId": "approve-po", "id": "row-7", "verb": "approve" }"""), out var byActionId));
        Assert.AreEqual("approve-po", ActionEvent(byActionId!).GetProperty("actionId").GetString());

        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(
            Json("""{ "id": "row-7", "verb": "approve" }"""), out var byId));
        Assert.AreEqual("row-7", ActionEvent(byId!).GetProperty("actionId").GetString());

        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(
            Json("""{ "verb": "approve" }"""), out var byVerb));
        Assert.AreEqual("approve", ActionEvent(byVerb!).GetProperty("actionId").GetString());

        // Non-string id values resolve through their JSON literal, as surface clients do.
        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(
            Json("""{ "id": 42 }"""), out var numericId));
        Assert.AreEqual("42", ActionEvent(numericId!).GetProperty("actionId").GetString());
    }

    [TestMethod]
    public void EmptySubmit_FallsBackToTheActionType()
    {
        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(Json("{}"), out var message));

        var actionEvent = ActionEvent(message!);
        Assert.AreEqual("Action.Submit", actionEvent.GetProperty("actionId").GetString());
        Assert.AreEqual(JsonValueKind.Null, actionEvent.GetProperty("verb").ValueKind);
        Assert.IsNull(message!.Message);
    }

    [TestMethod]
    public void MessageTemplate_IsExpandedIntoTheMessageText()
    {
        var value = Json("""
            {
              "verb": "approve",
              "messageTemplate": "User chose {verb} for {order.id} ({missing})",
              "order": { "id": "PO-1001" }
            }
            """);

        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(value, out var message));

        // Tokens resolve against the payload (dotted paths included); unmatched tokens stay
        // literal, mirroring the surface dispatcher.
        Assert.AreEqual("User chose approve for PO-1001 ({missing})", message!.Message);
        Assert.AreEqual(message.Message, ActionEvent(message).GetProperty("message").GetString());
    }

    [TestMethod]
    public void EnvelopeIdAndNestedPayload_AreCarried()
    {
        var value = Json("""{ "envelopeId": "approval-card", "order": { "id": "PO-1001", "amount": 250 } }""");

        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(value, out var message));

        var actionEvent = ActionEvent(message!);
        Assert.AreEqual("approval-card", actionEvent.GetProperty("envelopeId").GetString());

        var order = actionEvent.GetProperty("payload").GetProperty("order");
        Assert.AreEqual("PO-1001", order.GetProperty("id").GetString());
        Assert.AreEqual(250, order.GetProperty("amount").GetInt32());
    }

    [TestMethod]
    public void KeyLookup_IsCaseInsensitive()
    {
        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(
            Json("""{ "Verb": "approve" }"""), out var message));

        var actionEvent = ActionEvent(message!);
        Assert.AreEqual("approve", actionEvent.GetProperty("verb").GetString());
        Assert.AreEqual("approve", actionEvent.GetProperty("actionId").GetString());
    }

    [TestMethod]
    public void JsonStringValue_IsAccepted()
    {
        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(
            """{ "verb": "approve" }""", out var message));

        Assert.AreEqual("approve", ActionEvent(message!).GetProperty("verb").GetString());
    }

    [TestMethod]
    public void PlainObjectValue_IsAccepted()
    {
        var value = new Dictionary<string, object?> { ["verb"] = "approve", ["count"] = 3 };

        Assert.IsTrue(CopilotActivityMapper.TryCreateUiActionMessage(value, out var message));

        var actionEvent = ActionEvent(message!);
        Assert.AreEqual("approve", actionEvent.GetProperty("verb").GetString());
        Assert.AreEqual(3, actionEvent.GetProperty("payload").GetProperty("count").GetInt32());
    }

    [TestMethod]
    public void NonObjectValues_AreRejected()
    {
        Assert.IsFalse(CopilotActivityMapper.TryCreateUiActionMessage(null, out _));
        Assert.IsFalse(CopilotActivityMapper.TryCreateUiActionMessage(Json("[1, 2]"), out _));
        Assert.IsFalse(CopilotActivityMapper.TryCreateUiActionMessage(Json("42"), out _));
        Assert.IsFalse(CopilotActivityMapper.TryCreateUiActionMessage("not json at all", out _));
    }
}
