using System.Security.Claims;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.WebSockets;
using FabrCore.Host.WebSocket;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class WebSocketProtocolTests
{
    [TestMethod]
    public void Frame_Serializes_WithCamelCaseEnvelope()
    {
        var frame = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Request, new AgentMessage
        {
            ToHandle = "agent",
            Message = "hello",
        });
        frame.Id = "request-1";
        frame.CorrelationId = "parent-1";
        frame.Operation = FabrCoreWebSocketOperations.MessageSend;
        frame.DeliveryMode = FabrCoreWebSocketDeliveryModes.Async;

        var json = JsonSerializer.Serialize(frame, FabrCoreWebSocketProtocol.JsonOptions);

        StringAssert.Contains(json, "\"version\":\"2.0\"");
        StringAssert.Contains(json, "\"correlationId\"");
        StringAssert.Contains(json, "\"deliveryMode\":\"async\"");
        Assert.IsFalse(json.Contains("MessageType", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SupportedOperations_DoNotExposeAgentCreationOrProvisioning()
    {
        var values = typeof(FabrCoreWebSocketOperations).GetFields()
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        CollectionAssert.DoesNotContain(values, "agent.create");
        CollectionAssert.DoesNotContain(values, "createagent");
        CollectionAssert.DoesNotContain(values, "blueprint.apply");
        Assert.AreEqual(7, values.Length);
    }

    [TestMethod]
    public void PrincipalResolver_UsesClaimPrecedenceAndSurfaceNormalization()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "SUB Value"),
            new Claim("oid", "OID/Value"),
            new Claim(ClaimTypes.NameIdentifier, " Alice@Example.COM "),
        }, "test");

        var resolved = new DefaultWebSocketPrincipalResolver().Resolve(new ClaimsPrincipal(identity));

        Assert.AreEqual("alice-example.com", resolved);
    }

    [TestMethod]
    public void PrincipalResolver_RejectsUnauthenticatedIdentity()
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "alice") });
        Assert.IsNull(new DefaultWebSocketPrincipalResolver().Resolve(new ClaimsPrincipal(identity)));
    }

    [TestMethod]
    public void DeliveryFrame_RoundTripsSequenceAndMessage()
    {
        var frame = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Delivery,
            new AgentMessage { Id = "message-1", MessageType = "ui.render" });
        frame.Sequence = 42;
        frame.DeliveryId = "delivery-42";

        var copy = JsonSerializer.Deserialize<FabrCoreWebSocketFrame>(
            JsonSerializer.Serialize(frame, FabrCoreWebSocketProtocol.JsonOptions),
            FabrCoreWebSocketProtocol.JsonOptions)!;

        Assert.AreEqual(42, copy.Sequence);
        Assert.AreEqual("delivery-42", copy.DeliveryId);
        Assert.AreEqual("message-1", copy.Payload!.Value.Deserialize<AgentMessage>(FabrCoreWebSocketProtocol.JsonOptions)!.Id);
    }
}
