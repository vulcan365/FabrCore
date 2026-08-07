using FabrCore.Core;
using FabrCore.Core.WebSockets;

namespace FabrCore.Client.WebSocket;

public interface IFabrCoreWebSocketClient : IAsyncDisposable
{
    string ClientId { get; }
    event EventHandler<FabrCoreWebSocketGapEventArgs>? ResyncRequired;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<FabrCoreWebSocketAccepted> SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default);
    Task<AgentMessage> SendMessageAndReceiveAsync(AgentMessage message, CancellationToken cancellationToken = default);
    Task<FabrCoreWebSocketAccepted> SendEventAsync(EventMessage message, CancellationToken cancellationToken = default);
    Task<AgentHealthStatus> ResetAgentAsync(string handle, CancellationToken cancellationToken = default);
    Task<AgentHealthStatus> GetAgentHealthAsync(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackedAgentInfo>> GetTrackedAgentsAsync(bool activate = false, CancellationToken cancellationToken = default);
    Task<bool> IsAgentTrackedAsync(string handle, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentInfo>> GetSharedAgentsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<FabrCoreWebSocketDelivery> ReadDeliveriesAsync(CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(long sequence, CancellationToken cancellationToken = default);
}

public sealed record FabrCoreWebSocketDelivery(long Sequence, string DeliveryId, AgentMessage Message);

public sealed class FabrCoreWebSocketGapEventArgs(FabrCoreWebSocketGap gap) : EventArgs
{
    public FabrCoreWebSocketGap Gap { get; } = gap;
}

public sealed class FabrCoreWebSocketClientOptions
{
    public required Uri HostUri { get; init; }
    public required string ClientId { get; init; }
    public Uri TicketPath { get; init; } = new("/fabrcoreapi/ws/ticket", UriKind.Relative);
    public Uri WebSocketPath { get; init; } = new("/ws", UriKind.Relative);
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class FabrCoreWebSocketException(string code, string message, bool retryable)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
