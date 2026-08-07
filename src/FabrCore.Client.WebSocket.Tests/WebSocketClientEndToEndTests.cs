using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Client.WebSocket.Tests;

[TestClass]
public sealed class WebSocketClientEndToEndTests
{
    [TestMethod]
    public async Task ClientCompletesLiveFlowAndReconnectsWithCheckpoint()
    {
        var tickets = 0;
        var connections = 0;
        var checkpoints = new ConcurrentQueue<long?>();
        var acknowledged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.MapPost("/fabrcoreapi/ws/ticket", () =>
        {
            Interlocked.Increment(ref tickets);
            return Results.Json(new FabrCoreWebSocketTicketResponse($"ticket-{tickets}", DateTimeOffset.UtcNow.AddSeconds(30)), FabrCoreWebSocketProtocol.JsonOptions);
        });
        app.Map("/ws", async context =>
        {
            var connection = Interlocked.Increment(ref connections);
            var socket = await context.WebSockets.AcceptWebSocketAsync(FabrCoreWebSocketProtocol.Subprotocol);
            var helloFrame = await ReceiveAsync(socket, context.RequestAborted);
            var hello = helloFrame.Payload!.Value.Deserialize<FabrCoreWebSocketHello>(FabrCoreWebSocketProtocol.JsonOptions)!;
            checkpoints.Enqueue(hello.Checkpoint);
            await SendAsync(socket, FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Welcome,
                new FabrCoreWebSocketWelcome("alice", connection == 1 ? 0 : 3, connection == 1 ? null : 3, 0)), context.RequestAborted);

            if (connection == 1)
            {
                while (socket.State == WebSocketState.Open)
                {
                    var request = await ReceiveAsync(socket, context.RequestAborted);
                    if (request.Type == FabrCoreWebSocketFrameTypes.Ack)
                    {
                        acknowledged.TrySetResult(request.Sequence!.Value);
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None);
                        break;
                    }

                    object responsePayload = request.Operation switch
                    {
                        FabrCoreWebSocketOperations.AgentsTrackedList => new List<TrackedAgentInfo>(),
                        FabrCoreWebSocketOperations.AgentHealthGet => new AgentHealthStatus
                        {
                            Handle = "alice:assistant",
                            State = HealthState.Healthy,
                            Timestamp = DateTime.UtcNow,
                            IsConfigured = true,
                        },
                        FabrCoreWebSocketOperations.MessageSend => new FabrCoreWebSocketAccepted(),
                        _ => throw new InvalidOperationException(request.Operation),
                    };
                    var response = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Response, responsePayload);
                    response.CorrelationId = request.Id;
                    response.Operation = request.Operation;
                    await SendAsync(socket, response, context.RequestAborted);

                    if (request.Operation == FabrCoreWebSocketOperations.MessageSend)
                    {
                        await SendDeliveryAsync(socket, 1, "thinking", "_thinking", context.RequestAborted);
                        await SendDeliveryAsync(socket, 2, "render", "ui.render", context.RequestAborted);
                        await SendDeliveryAsync(socket, 3, "final", null, context.RequestAborted);
                    }
                }
            }
            else
            {
                var gap = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Gap, new FabrCoreWebSocketGap(1, 3, 3));
                await SendAsync(socket, gap, context.RequestAborted);
                await SendDeliveryAsync(socket, 3, "final", null, context.RequestAborted);
                reconnected.TrySetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted).SuppressCancellationThrow();
            }
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            var client = new FabrCoreWebSocketClient(http, new FabrCoreWebSocketClientOptions
            {
                HostUri = new Uri(address),
                ClientId = "desktop-stable",
                InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
                MaximumReconnectDelay = TimeSpan.FromMilliseconds(100),
            });
            var gapRaised = new TaskCompletionSource<FabrCoreWebSocketGap>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ResyncRequired += (_, args) => gapRaised.TrySetResult(args.Gap);
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(0, (await client.GetTrackedAgentsAsync()).Count);
            Assert.AreEqual(HealthState.Healthy, (await client.GetAgentHealthAsync("assistant")).State);
            await client.SendMessageAsync(new AgentMessage { ToHandle = "assistant", Kind = MessageKind.Request });

            using var deliveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = new List<FabrCoreWebSocketDelivery>();
            await foreach (var delivery in client.ReadDeliveriesAsync(deliveryTimeout.Token))
            {
                received.Add(delivery);
                if (received.Count == 3)
                    break;
            }
            CollectionAssert.AreEqual(new[] { "_thinking", "ui.render", null }, received.Select(x => x.Message.MessageType).ToArray());
            await client.AcknowledgeAsync(3);
            Assert.AreEqual(3, await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, (await gapRaised.Task.WaitAsync(TimeSpan.FromSeconds(5))).RequestedSequence);

            var replay = await client.ReadDeliveriesAsync(deliveryTimeout.Token).FirstAsync(deliveryTimeout.Token);
            Assert.AreEqual(3, replay.Sequence);
            Assert.IsTrue(tickets >= 2, "Reconnect must obtain a fresh ticket.");
            CollectionAssert.AreEqual(new long?[] { null, 3 }, checkpoints.Take(2).ToArray());
            await client.DisposeAsync();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static Task SendDeliveryAsync(System.Net.WebSockets.WebSocket socket, long sequence, string id, string? messageType, CancellationToken cancellationToken)
    {
        var frame = FabrCoreWebSocketFrame.Create(FabrCoreWebSocketFrameTypes.Delivery,
            new AgentMessage { Id = id, MessageType = messageType, Message = id });
        frame.Sequence = sequence;
        frame.DeliveryId = $"delivery-{sequence}";
        return SendAsync(socket, frame, cancellationToken);
    }

    private static async Task SendAsync(System.Net.WebSockets.WebSocket socket, FabrCoreWebSocketFrame frame, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, FabrCoreWebSocketProtocol.JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<FabrCoreWebSocketFrame> ReceiveAsync(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        return JsonSerializer.Deserialize<FabrCoreWebSocketFrame>(buffer.AsSpan(0, result.Count), FabrCoreWebSocketProtocol.JsonOptions)!;
    }
}

internal static class AsyncEnumerableTestExtensions
{
    public static async Task<T> FirstAsync<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken))
            return item;
        throw new InvalidOperationException("The sequence was empty.");
    }

    public static async Task SuppressCancellationThrow(this Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
