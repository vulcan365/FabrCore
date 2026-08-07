using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.Interfaces;
using FabrCore.Core.WebSockets;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Host.WebSocket;

/// <summary>One authenticated FabrCore WebSocket v2 session.</summary>
public sealed class WebSocketSession : IPrincipalWebSocketObserver, IAsyncDisposable
{
    private readonly System.Net.WebSockets.WebSocket socket;
    private readonly IClusterClient clusterClient;
    private readonly ILogger<WebSocketSession> logger;
    private readonly IAuditProvider auditProvider;
    private readonly string principalHandle;
    private readonly FabrCoreHostOptions hostOptions;
    private readonly FabrCoreWebSocketOptions options;
    private readonly Channel<FabrCoreWebSocketFrame> outbound;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim concurrency;
    private readonly ConcurrentDictionary<string, Task> operations = new(StringComparer.Ordinal);
    private IPrincipalGrain? principalGrain;
    private IPrincipalWebSocketObserver? observerReference;
    private Task? outboundPump;
    private string? clientId;
    private readonly string connectionId = Guid.NewGuid().ToString("N");
    private int overloadClosing;

    public WebSocketSession(
        System.Net.WebSockets.WebSocket socket,
        IClusterClient clusterClient,
        ILogger<WebSocketSession> logger,
        IAuditProvider auditProvider,
        string principalHandle,
        FabrCoreHostOptions hostOptions,
        FabrCoreWebSocketOptions options)
    {
        this.socket = socket;
        this.clusterClient = clusterClient;
        this.logger = logger;
        this.auditProvider = auditProvider;
        this.principalHandle = principalHandle;
        this.hostOptions = hostOptions;
        this.options = options;
        concurrency = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
        outbound = Channel.CreateBounded<FabrCoreWebSocketFrame>(new BoundedChannelOptions(hostOptions.OutboundQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void SetInitialTraceContext(System.Diagnostics.ActivityContext context) { }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        outboundPump = RunOutboundPumpAsync(linked.Token);

        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        helloTimeout.CancelAfter(options.HelloTimeout);
        var first = await ReceiveFrameAsync(helloTimeout.Token);
        if (first is null || first.Type != FabrCoreWebSocketFrameTypes.Hello || first.Payload is null)
        {
            await ProtocolViolationAsync("hello_required", "The first frame must be hello.", linked.Token);
            return;
        }

        var hello = first.Payload.Value.Deserialize<FabrCoreWebSocketHello>(FabrCoreWebSocketProtocol.JsonOptions);
        if (hello is null || string.IsNullOrWhiteSpace(hello.ClientId))
        {
            await ProtocolViolationAsync("invalid_hello", "hello.payload.clientId is required.", linked.Token);
            return;
        }

        clientId = hello.ClientId;
        principalGrain = clusterClient.GetGrain<IPrincipalGrain>(principalHandle);
        observerReference = clusterClient.CreateObjectReference<IPrincipalWebSocketObserver>(this);
        var registration = await principalGrain.SubscribeWebSocket(observerReference, clientId, connectionId, hello.Checkpoint);

        await QueueAsync(FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Welcome,
            new FabrCoreWebSocketWelcome(principalHandle, registration.CurrentSequence,
                registration.OldestAvailableSequence, registration.Replay.Count)), linked.Token);

        if (registration.GapAfterSequence is long gapAfter && registration.OldestAvailableSequence is long oldest)
        {
            await QueueAsync(FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Gap,
                new FabrCoreWebSocketGap(gapAfter, oldest, registration.CurrentSequence)), linked.Token);
            await AuditAsync(AuditOutcome.Error, "websocket.replay.gap", "The client cursor fell behind retained deliveries.");
        }

        foreach (var delivery in registration.Replay.OrderBy(x => x.Sequence))
            await QueueAsync(ToDeliveryFrame(delivery), linked.Token);

        while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
        {
            var frame = await ReceiveFrameAsync(linked.Token);
            if (frame is null)
                break;
            if (frame.Type == FabrCoreWebSocketFrameTypes.Ack)
            {
                if (frame.Sequence is not long sequence)
                    await QueueErrorAsync(frame.Id, null, "invalid_ack", "ack.sequence is required.", false, linked.Token);
                else
                    await principalGrain.AcknowledgeWebSocket(clientId, sequence);
                continue;
            }
            if (frame.Type != FabrCoreWebSocketFrameTypes.Request)
            {
                await ProtocolViolationAsync("invalid_frame_type", "Only request and ack frames are valid after hello.", linked.Token);
                return;
            }

            var operationKey = frame.Id ?? Guid.NewGuid().ToString("N");
            var task = HandleRequestAsync(frame, linked.Token);
            operations[operationKey] = task;
            _ = task.ContinueWith(completedTask => operations.TryRemove(operationKey, out var ignored), TaskScheduler.Default);
        }
    }

    private async Task HandleRequestAsync(FabrCoreWebSocketFrame request, CancellationToken sessionToken)
    {
        await concurrency.WaitAsync(sessionToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            timeout.CancelAfter(options.RequestTimeout);
            if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Operation))
            {
                await QueueErrorAsync(request.Id, request.Operation, "invalid_request", "request.id and request.operation are required.", false, timeout.Token);
                return;
            }

            var result = await DispatchRequestAsync(request, timeout.Token).WaitAsync(timeout.Token);
            await QueueResponseAsync(request, result, timeout.Token);
        }
        catch (UnsupportedWebSocketOperationException ex)
        {
            await QueueErrorAsync(request.Id, request.Operation, "unsupported_operation", ex.Message, false, sessionToken);
        }
        catch (AclDeniedException ex)
        {
            await QueueErrorAsync(request.Id, request.Operation, "forbidden", ex.Message, false, sessionToken);
        }
        catch (UnauthorizedAccessException ex)
        {
            await QueueErrorAsync(request.Id, request.Operation, "forbidden", ex.Message, false, sessionToken);
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            await QueueErrorAsync(request.Id, request.Operation, "timeout", "The operation timed out.", true, sessionToken);
        }
        catch (ArgumentException ex)
        {
            await QueueErrorAsync(request.Id, request.Operation, "invalid_argument", ex.Message, false, sessionToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WebSocket operation {Operation} failed for principal {Principal}", request.Operation, principalHandle);
            await QueueErrorAsync(request.Id, request.Operation, "internal_error", "The operation failed.", false, sessionToken);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private Task<object?> DispatchRequestAsync(FabrCoreWebSocketFrame request, CancellationToken cancellationToken) =>
        new WebSocketOperationDispatcher(principalGrain!, principalHandle).DispatchAsync(request);


    public void OnDelivery(FabrCoreWebSocketDeliveryRecord delivery)
    {
        if (!outbound.Writer.TryWrite(ToDeliveryFrame(delivery)))
            _ = CloseForOverloadAsync();
    }

    private static FabrCoreWebSocketFrame ToDeliveryFrame(FabrCoreWebSocketDeliveryRecord delivery)
    {
        var frame = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Delivery, delivery.Message);
        frame.Sequence = delivery.Sequence;
        frame.DeliveryId = delivery.DeliveryId;
        return frame;
    }

    private async Task<FabrCoreWebSocketFrame?> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var memory = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException(WebSocketError.InvalidMessageType);
            if (memory.Length + result.Count > hostOptions.MaxIncomingMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Frame is too large.", cancellationToken);
                return null;
            }
            memory.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        try
        {
            var bytes = memory.ToArray();
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("version", out var version) ||
                version.GetString() != FabrCoreWebSocketProtocol.Version)
                throw new JsonException("Unsupported or missing protocol version.");
            var frame = JsonSerializer.Deserialize<FabrCoreWebSocketFrame>(bytes, FabrCoreWebSocketProtocol.JsonOptions);
            if (frame is null || frame.Version != FabrCoreWebSocketProtocol.Version)
                throw new JsonException("Unsupported or missing protocol version.");
            return frame;
        }
        catch (JsonException ex)
        {
            await ProtocolViolationAsync("invalid_frame", ex.Message, cancellationToken);
            return null;
        }
    }

    private async Task RunOutboundPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in outbound.Reader.ReadAllAsync(cancellationToken))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, FabrCoreWebSocketProtocol.JsonOptions);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { stopping.Cancel(); }
    }

    private ValueTask QueueAsync(FabrCoreWebSocketFrame frame, CancellationToken cancellationToken) =>
        outbound.Writer.WriteAsync(frame, cancellationToken);

    private Task QueueResponseAsync(FabrCoreWebSocketFrame request, object? result, CancellationToken cancellationToken)
    {
        var frame = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Response, result);
        frame.CorrelationId = request.Id;
        frame.Operation = request.Operation;
        return QueueAsync(frame, cancellationToken).AsTask();
    }

    private Task QueueErrorAsync(string? correlationId, string? operation, string code, string message, bool retryable, CancellationToken cancellationToken)
    {
        var frame = new FabrCoreWebSocketFrame
        {
            Type = FabrCoreWebSocketFrameTypes.Response,
            CorrelationId = correlationId,
            Operation = operation,
            Error = new FabrCoreWebSocketError(code, message, retryable),
        };
        return QueueAsync(frame, cancellationToken).AsTask();
    }

    private async Task ProtocolViolationAsync(string code, string message, CancellationToken cancellationToken)
    {
        await AuditAsync(AuditOutcome.Denied, $"websocket.protocol.{code}", message);
        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, message, cancellationToken);
        stopping.Cancel();
    }

    private async Task CloseForOverloadAsync()
    {
        if (Interlocked.Exchange(ref overloadClosing, 1) != 0)
            return;
        await AuditAsync(AuditOutcome.Error, "websocket.overload", "Outbound queue capacity was exceeded.");
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync((WebSocketCloseStatus)1013, "Try again later.", CancellationToken.None);
        }
        catch (WebSocketException) { }
        stopping.Cancel();
    }

    private Task AuditAsync(AuditOutcome outcome, string permission, string reason) => auditProvider.RecordAsync(new AuditEvent
    {
        Category = AuditCategory.WebSocketSecurity,
        Outcome = outcome,
        SubjectPrincipal = principalHandle,
        Permission = permission,
        Reason = reason,
        WasEnforced = outcome == AuditOutcome.Denied,
    });

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        outbound.Writer.TryComplete();
        if (principalGrain is not null && clientId is not null)
        {
            try { await principalGrain.UnsubscribeWebSocket(clientId, connectionId); }
            catch (Exception ex) { logger.LogDebug(ex, "WebSocket unsubscribe failed during disposal."); }
        }
        try { await Task.WhenAll(operations.Values).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { }
        if (outboundPump is not null)
        {
            try { await outboundPump.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
        }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session closed.", CancellationToken.None); }
            catch (WebSocketException) { }
        }
        socket.Dispose();
        concurrency.Dispose();
        stopping.Dispose();
    }

}
