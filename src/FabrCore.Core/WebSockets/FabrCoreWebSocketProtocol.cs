using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Core.WebSockets;

public static class FabrCoreWebSocketProtocol
{
    public const string Version = "2.0";
    public const string Subprotocol = "fabrcore.v2";
    public const string TicketSubprotocolPrefix = "fabrcore.ticket.";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };
}

public static class FabrCoreWebSocketFrameTypes
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Request = "request";
    public const string Response = "response";
    public const string Delivery = "delivery";
    public const string Ack = "ack";
    public const string Gap = "gap";
}

public static class FabrCoreWebSocketOperations
{
    public const string MessageSend = "message.send";
    public const string EventSend = "event.send";
    public const string AgentReset = "agent.reset";
    public const string AgentHealthGet = "agent.health.get";
    public const string AgentsTrackedList = "agents.tracked.list";
    public const string AgentTrackedCheck = "agent.tracked.check";
    public const string AgentsSharedList = "agents.shared.list";
}

public static class FabrCoreWebSocketDeliveryModes
{
    public const string Async = "async";
    public const string RequestResponse = "requestResponse";
}

public sealed class FabrCoreWebSocketFrame
{
    public string Version { get; set; } = FabrCoreWebSocketProtocol.Version;
    public required string Type { get; set; }
    public string? Id { get; set; }
    public string? CorrelationId { get; set; }
    public string? Operation { get; set; }
    public string? DeliveryMode { get; set; }
    public long? Sequence { get; set; }
    public string? DeliveryId { get; set; }
    public JsonElement? Payload { get; set; }
    public FabrCoreWebSocketError? Error { get; set; }

    public static FabrCoreWebSocketFrame Create<T>(string type, T? payload = default) => new()
    {
        Type = type,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, FabrCoreWebSocketProtocol.JsonOptions),
    };
}

public sealed record FabrCoreWebSocketError(string Code, string Message, bool Retryable = false);
public sealed record FabrCoreWebSocketHello(string ClientId, long? Checkpoint = null);
public sealed record FabrCoreWebSocketWelcome(string PrincipalHandle, long CurrentSequence, long? OldestAvailableSequence, int ReplayCount);
public sealed record FabrCoreWebSocketGap(long RequestedSequence, long OldestAvailableSequence, long LatestAvailableSequence);
public sealed record FabrCoreWebSocketAccepted(bool Accepted = true);
public sealed record FabrCoreWebSocketHandleRequest(string Handle);
public sealed record FabrCoreWebSocketHealthRequest(string Handle, HealthDetailLevel DetailLevel = HealthDetailLevel.Basic);
public sealed record FabrCoreWebSocketTrackedListRequest(bool Activate = false);
public sealed record FabrCoreWebSocketTrackedCheckResult(bool Tracked);
public sealed record FabrCoreWebSocketTicketResponse(string Ticket, DateTimeOffset ExpiresAt);

[GenerateSerializer]
public sealed class FabrCoreWebSocketDeliveryRecord
{
    [Id(0)] public long Sequence { get; set; }
    [Id(1)] public string DeliveryId { get; set; } = Guid.NewGuid().ToString("N");
    [Id(2)] public AgentMessage Message { get; set; } = new();
    [Id(3)] public DateTimeOffset CreatedAt { get; set; }
}

[GenerateSerializer]
public sealed class FabrCoreWebSocketClientCursor
{
    [Id(0)] public long AcknowledgedSequence { get; set; }
    [Id(1)] public long HighestDeliveredSequence { get; set; }
    [Id(2)] public DateTimeOffset LastSeenAt { get; set; }
}

[GenerateSerializer]
public sealed class FabrCoreWebSocketDeliveryState
{
    [Id(0)] public long CurrentSequence { get; set; }
    [Id(1)] public List<FabrCoreWebSocketDeliveryRecord> Deliveries { get; set; } = [];
    [Id(2)] public Dictionary<string, FabrCoreWebSocketClientCursor> Clients { get; set; } = new(StringComparer.Ordinal);
}

[GenerateSerializer]
public sealed class FabrCoreWebSocketRegistration
{
    [Id(0)] public long CurrentSequence { get; set; }
    [Id(1)] public long? OldestAvailableSequence { get; set; }
    [Id(2)] public long? GapAfterSequence { get; set; }
    [Id(3)] public List<FabrCoreWebSocketDeliveryRecord> Replay { get; set; } = [];
}
