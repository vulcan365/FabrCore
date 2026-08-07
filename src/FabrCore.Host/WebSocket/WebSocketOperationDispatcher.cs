using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Core.WebSockets;

namespace FabrCore.Host.WebSocket;

internal sealed class WebSocketOperationDispatcher(IPrincipalGrain principalGrain, string principalHandle)
{
    public async Task<object?> DispatchAsync(FabrCoreWebSocketFrame request)
    {
        return request.Operation switch
        {
            FabrCoreWebSocketOperations.MessageSend => await SendMessageAsync(request),
            FabrCoreWebSocketOperations.EventSend => await SendEventAsync(request),
            FabrCoreWebSocketOperations.AgentReset => await principalGrain.ResetAgent(Payload<FabrCoreWebSocketHandleRequest>(request).Handle),
            FabrCoreWebSocketOperations.AgentHealthGet => await GetHealthAsync(request),
            FabrCoreWebSocketOperations.AgentsTrackedList => await GetTrackedAsync(request),
            FabrCoreWebSocketOperations.AgentTrackedCheck => new FabrCoreWebSocketTrackedCheckResult(
                await principalGrain.IsAgentTracked(Payload<FabrCoreWebSocketHandleRequest>(request).Handle)),
            FabrCoreWebSocketOperations.AgentsSharedList => await principalGrain.GetAccessibleSharedAgents(),
            _ => throw new UnsupportedWebSocketOperationException(request.Operation ?? "(missing)"),
        };
    }

    private async Task<object> SendMessageAsync(FabrCoreWebSocketFrame request)
    {
        var message = Payload<AgentMessage>(request);
        message.FromHandle = principalHandle;
        if (request.DeliveryMode == FabrCoreWebSocketDeliveryModes.Async)
        {
            await principalGrain.SendMessage(message);
            return new FabrCoreWebSocketAccepted();
        }
        if (request.DeliveryMode == FabrCoreWebSocketDeliveryModes.RequestResponse)
            return await principalGrain.SendAndReceiveMessage(message);
        throw new ArgumentException("message.send requires deliveryMode 'async' or 'requestResponse'.");
    }

    private async Task<object> SendEventAsync(FabrCoreWebSocketFrame request)
    {
        var message = Payload<EventMessage>(request);
        message.Source = principalHandle;
        await principalGrain.SendEvent(message);
        return new FabrCoreWebSocketAccepted();
    }

    private async Task<AgentHealthStatus> GetHealthAsync(FabrCoreWebSocketFrame request)
    {
        var value = Payload<FabrCoreWebSocketHealthRequest>(request);
        return await principalGrain.GetAgentHealth(value.Handle, value.DetailLevel);
    }

    private Task<List<TrackedAgentInfo>> GetTrackedAsync(FabrCoreWebSocketFrame request)
    {
        var value = request.Payload is null
            ? new FabrCoreWebSocketTrackedListRequest()
            : Payload<FabrCoreWebSocketTrackedListRequest>(request);
        return principalGrain.GetTrackedAgents(value.Activate);
    }

    private static T Payload<T>(FabrCoreWebSocketFrame frame)
    {
        if (frame.Payload is not JsonElement payload)
            throw new ArgumentException($"A valid {typeof(T).Name} payload is required.");
        var value = payload.Deserialize<T>(FabrCoreWebSocketProtocol.JsonOptions);
        return value ?? throw new ArgumentException($"A valid {typeof(T).Name} payload is required.");
    }
}

internal sealed class UnsupportedWebSocketOperationException(string operation)
    : Exception($"WebSocket operation '{operation}' is not supported. Agent provisioning is available through HTTP and Blueprint APIs.");
