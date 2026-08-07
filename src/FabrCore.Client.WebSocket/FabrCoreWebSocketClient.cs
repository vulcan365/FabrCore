using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using FabrCore.Core;
using FabrCore.Core.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Client.WebSocket;

public sealed class FabrCoreWebSocketClient : IFabrCoreWebSocketClient
{
    private readonly HttpClient httpClient;
    private readonly FabrCoreWebSocketClientOptions options;
    private readonly IFabrCoreWebSocketCheckpointStore checkpointStore;
    private readonly ILogger<FabrCoreWebSocketClient> logger;
    private readonly Channel<FabrCoreWebSocketDelivery> deliveries = Channel.CreateUnbounded<FabrCoreWebSocketDelivery>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FabrCoreWebSocketFrame>> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private TaskCompletionSource<bool> firstConnection = NewConnectionSource();
    private ClientWebSocket? socket;
    private Task? connectionLoop;
    private long highestDelivery;
    private bool disposed;

    public FabrCoreWebSocketClient(
        HttpClient httpClient,
        FabrCoreWebSocketClientOptions options,
        IFabrCoreWebSocketCheckpointStore? checkpointStore = null,
        ILogger<FabrCoreWebSocketClient>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) || options.ClientId.Length > 128)
            throw new ArgumentException("A stable ClientId of at most 128 characters is required.", nameof(options));
        this.httpClient = httpClient;
        this.options = options;
        this.checkpointStore = checkpointStore ?? new InMemoryFabrCoreWebSocketCheckpointStore();
        this.logger = logger ?? NullLogger<FabrCoreWebSocketClient>.Instance;
    }

    public string ClientId => options.ClientId;
    public event EventHandler<FabrCoreWebSocketGapEventArgs>? ResyncRequired;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        connectionLoop ??= RunConnectionLoopAsync(stopping.Token);
        await firstConnection.Task.WaitAsync(cancellationToken);
    }

    public Task<FabrCoreWebSocketAccepted> SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default) =>
        RequestAsync<FabrCoreWebSocketAccepted>(FabrCoreWebSocketOperations.MessageSend, message, FabrCoreWebSocketDeliveryModes.Async, cancellationToken);

    public Task<AgentMessage> SendMessageAndReceiveAsync(AgentMessage message, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentMessage>(FabrCoreWebSocketOperations.MessageSend, message, FabrCoreWebSocketDeliveryModes.RequestResponse, cancellationToken);

    public Task<FabrCoreWebSocketAccepted> SendEventAsync(EventMessage message, CancellationToken cancellationToken = default) =>
        RequestAsync<FabrCoreWebSocketAccepted>(FabrCoreWebSocketOperations.EventSend, message, null, cancellationToken);

    public Task<AgentHealthStatus> ResetAgentAsync(string handle, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentHealthStatus>(FabrCoreWebSocketOperations.AgentReset, new FabrCoreWebSocketHandleRequest(handle), null, cancellationToken);

    public Task<AgentHealthStatus> GetAgentHealthAsync(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentHealthStatus>(FabrCoreWebSocketOperations.AgentHealthGet, new FabrCoreWebSocketHealthRequest(handle, detailLevel), null, cancellationToken);

    public async Task<IReadOnlyList<TrackedAgentInfo>> GetTrackedAgentsAsync(bool activate = false, CancellationToken cancellationToken = default) =>
        await RequestAsync<List<TrackedAgentInfo>>(FabrCoreWebSocketOperations.AgentsTrackedList, new FabrCoreWebSocketTrackedListRequest(activate), null, cancellationToken);

    public async Task<bool> IsAgentTrackedAsync(string handle, CancellationToken cancellationToken = default) =>
        (await RequestAsync<FabrCoreWebSocketTrackedCheckResult>(FabrCoreWebSocketOperations.AgentTrackedCheck, new FabrCoreWebSocketHandleRequest(handle), null, cancellationToken)).Tracked;

    public async Task<IReadOnlyList<AgentInfo>> GetSharedAgentsAsync(CancellationToken cancellationToken = default) =>
        await RequestAsync<List<AgentInfo>>(FabrCoreWebSocketOperations.AgentsSharedList, new { }, null, cancellationToken);

    public IAsyncEnumerable<FabrCoreWebSocketDelivery> ReadDeliveriesAsync(CancellationToken cancellationToken = default) =>
        deliveries.Reader.ReadAllAsync(cancellationToken);

    public async Task AcknowledgeAsync(long sequence, CancellationToken cancellationToken = default)
    {
        if (sequence < 0 || sequence > Interlocked.Read(ref highestDelivery))
            throw new ArgumentOutOfRangeException(nameof(sequence));
        await SendFrameAsync(new FabrCoreWebSocketFrame { Type = FabrCoreWebSocketFrameTypes.Ack, Sequence = sequence }, cancellationToken);
        await checkpointStore.SetCheckpointAsync(ClientId, sequence, cancellationToken);
    }

    private async Task<T> RequestAsync<T>(string operation, object payload, string? deliveryMode, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<FabrCoreWebSocketFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
            throw new InvalidOperationException("Could not reserve request correlation id.");
        try
        {
            var frame = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Request, payload);
            frame.Id = id;
            frame.Operation = operation;
            frame.DeliveryMode = deliveryMode;
            await SendFrameAsync(frame, cancellationToken);
            var response = await completion.Task.WaitAsync(cancellationToken);
            if (response.Error is not null)
                throw new FabrCoreWebSocketException(response.Error.Code, response.Error.Message, response.Error.Retryable);
            if (response.Payload is not JsonElement responsePayload)
                throw new FabrCoreWebSocketException("invalid_response", "The response did not contain a payload.", false);
            return responsePayload.Deserialize<T>(FabrCoreWebSocketProtocol.JsonOptions)
                ?? throw new FabrCoreWebSocketException("invalid_response", "The response payload was invalid.", false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var delay = options.InitialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(cancellationToken);
                delay = options.InitialReconnectDelay;
                await ReceiveLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FabrCore WebSocket disconnected; reconnecting.");
                FailPending(ex);
            }

            socket?.Dispose();
            socket = null;
            if (cancellationToken.IsCancellationRequested)
                break;
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            await Task.Delay(TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitter), cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(options.MaximumReconnectDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
        }
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(new Uri(options.HostUri, options.TicketPath), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var ticket = await response.Content.ReadFromJsonAsync<FabrCoreWebSocketTicketResponse>(FabrCoreWebSocketProtocol.JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Ticket endpoint returned an invalid response.");

        var next = new ClientWebSocket();
        next.Options.AddSubProtocol(FabrCoreWebSocketProtocol.Subprotocol);
        next.Options.AddSubProtocol(FabrCoreWebSocketProtocol.TicketSubprotocolPrefix + ticket.Ticket);
        var uri = new Uri(options.HostUri, options.WebSocketPath);
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" };
        await next.ConnectAsync(builder.Uri, cancellationToken);
        if (next.SubProtocol != FabrCoreWebSocketProtocol.Subprotocol)
            throw new WebSocketException("The server did not negotiate fabrcore.v2.");
        socket = next;

        var checkpoint = await checkpointStore.GetCheckpointAsync(ClientId, cancellationToken);
        await SendFrameAsync(FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Hello,
            new FabrCoreWebSocketHello(ClientId, checkpoint)), cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (socket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var frame = await ReceiveFrameAsync(socket, cancellationToken);
            switch (frame.Type)
            {
                case FabrCoreWebSocketFrameTypes.Welcome:
                    firstConnection.TrySetResult(true);
                    break;
                case FabrCoreWebSocketFrameTypes.Response when frame.CorrelationId is not null:
                    if (pending.TryGetValue(frame.CorrelationId, out var completion))
                        completion.TrySetResult(frame);
                    break;
                case FabrCoreWebSocketFrameTypes.Delivery when frame.Sequence is long sequence && frame.DeliveryId is not null && frame.Payload is JsonElement payload:
                    var message = payload.Deserialize<AgentMessage>(FabrCoreWebSocketProtocol.JsonOptions)
                        ?? throw new JsonException("Invalid delivery payload.");
                    Interlocked.Exchange(ref highestDelivery, Math.Max(Interlocked.Read(ref highestDelivery), sequence));
                    await deliveries.Writer.WriteAsync(new FabrCoreWebSocketDelivery(sequence, frame.DeliveryId, message), cancellationToken);
                    break;
                case FabrCoreWebSocketFrameTypes.Gap when frame.Payload is JsonElement gapPayload:
                    var gap = gapPayload.Deserialize<FabrCoreWebSocketGap>(FabrCoreWebSocketProtocol.JsonOptions)
                        ?? throw new JsonException("Invalid gap payload.");
                    ResyncRequired?.Invoke(this, new FabrCoreWebSocketGapEventArgs(gap));
                    break;
            }
        }
        throw new WebSocketException("The FabrCore WebSocket closed.");
    }

    private async Task SendFrameAsync(FabrCoreWebSocketFrame frame, CancellationToken cancellationToken)
    {
        var active = socket;
        if (active?.State != WebSocketState.Open)
            throw new InvalidOperationException("The FabrCore WebSocket is not connected.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, FabrCoreWebSocketProtocol.JsonOptions);
        await sendLock.WaitAsync(cancellationToken);
        try { await active.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { sendLock.Release(); }
    }

    private static async Task<FabrCoreWebSocketFrame> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var memory = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("The server closed the FabrCore WebSocket.");
            memory.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<FabrCoreWebSocketFrame>(memory.ToArray(), FabrCoreWebSocketProtocol.JsonOptions)
            ?? throw new JsonException("Invalid FabrCore WebSocket frame.");
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in pending.Values)
            completion.TrySetException(new IOException("The connection ended while the operation outcome was indeterminate.", exception));
    }

    private static TaskCompletionSource<bool> NewConnectionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        deliveries.Writer.TryComplete();
        if (connectionLoop is not null)
        {
            try { await connectionLoop; }
            catch (OperationCanceledException) { }
        }
        socket?.Dispose();
        sendLock.Dispose();
        stopping.Dispose();
    }
}
