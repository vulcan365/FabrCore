using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Swarm;
using FabrCore.Surface.Contracts;

namespace FabrCore.Surface.Ai.Swarm;

public sealed class SurfaceSwarmSquadConversationBus
{
    private readonly IFabrCoreAgentHost agentHost;
    private readonly SurfaceSwarmSquadRuntime runtime;

    public SurfaceSwarmSquadConversationBus(IFabrCoreAgentHost agentHost, SurfaceSwarmSquadRuntime runtime)
    {
        this.agentHost = agentHost;
        this.runtime = runtime;
    }

    public async Task<AgentMessage> SendAndReceiveAsync(AgentMessage request, TimeSpan? timeout = null)
    {
        Stamp(request);
        await MirrorAsync(request, request.MessageType ?? SurfaceSwarmMessageTypes.TaskDispatch);

        AgentMessage response;
        if (timeout is { } limit && limit > TimeSpan.Zero)
        {
            var sendTask = agentHost.SendAndReceiveMessage(request);
            var completed = await Task.WhenAny(sendTask, Task.Delay(limit));
            if (completed != sendTask)
            {
                throw new TimeoutException(
                    $"No response from '{request.ToHandle}' within {limit.TotalSeconds:0}s.");
            }

            response = await sendTask;
        }
        else
        {
            response = await agentHost.SendAndReceiveMessage(request);
        }

        Stamp(response);
        response.Args![SurfaceSwarmArgs.SquadHandle] = runtime.Squad.OrchestratorHandle;

        // Card responses keep their real message type so the mirrored internal
        // record still classifies (and renders) as an Adaptive Card when expanded.
        await MirrorAsync(response, IsAdaptiveCardRender(response) ? response.MessageType : SurfaceSwarmMessageTypes.TaskResult);
        await ForwardCardToPrincipalAsync(response);
        return response;
    }

    /// <summary>
    /// True when a message carries a Surface Adaptive Card render payload.
    /// </summary>
    public static bool IsAdaptiveCardRender(AgentMessage message)
        => string.Equals(message.MessageType, SurfaceMessageTypes.UiRender, StringComparison.OrdinalIgnoreCase)
           && string.Equals(message.DataType, SurfaceMessageTypes.DataType, StringComparison.OrdinalIgnoreCase)
           && message.Data is { Length: > 0 };

    /// <summary>
    /// Delivers a member's Adaptive Card render to the principal as a first-class
    /// card message. The card keeps the member as its sender so card actions that
    /// do not carry an explicit targetAgent round-trip to the originating agent,
    /// never to the squad shell that relayed it. The envelope payload is passed
    /// through untouched so explicit routeTo/targetAgent metadata keeps working.
    /// </summary>
    private async Task ForwardCardToPrincipalAsync(AgentMessage response)
    {
        if (!IsAdaptiveCardRender(response) || string.IsNullOrWhiteSpace(runtime.Squad.PrincipalHandle))
        {
            return;
        }

        var card = new AgentMessage
        {
            ToHandle = runtime.Squad.PrincipalHandle,
            FromHandle = response.FromHandle,
            Channel = runtime.Squad.OrchestratorHandle,
            MessageType = response.MessageType,
            Message = response.Message,
            Kind = MessageKind.Response,
            DataType = response.DataType,
            Data = response.Data,
            Files = [.. response.Files],
            State = response.State is null ? new Dictionary<string, string>() : new Dictionary<string, string>(response.State),
            Args = response.Args is null ? new Dictionary<string, string>() : new Dictionary<string, string>(response.Args),
            TraceId = response.TraceId
        };

        Stamp(card);

        // Not internal traffic: no mirror flag and no original-handle args, so the
        // transcript renders it as a visible card instead of collapsed squad chatter.
        card.Args!.Remove(SurfaceSwarmArgs.Mirror);
        card.Args.Remove(SurfaceSquadArgs.Mirror);
        card.Args.Remove(SurfaceSwarmArgs.OriginalFromHandle);
        card.Args.Remove(SurfaceSquadArgs.OriginalFromHandle);
        card.Args.Remove(SurfaceSwarmArgs.OriginalToHandle);
        card.Args.Remove(SurfaceSquadArgs.OriginalToHandle);

        await agentHost.SendMessage(card);
    }

    public async Task SendAsync(AgentMessage request)
    {
        Stamp(request);
        await MirrorAsync(request, request.MessageType ?? SurfaceSwarmMessageTypes.Chat);
        await agentHost.SendMessage(request);
    }

    public Task MirrorAsync(AgentMessage message, string? messageType = null)
    {
        if (string.IsNullOrWhiteSpace(runtime.Squad.PrincipalHandle))
        {
            return Task.CompletedTask;
        }

        var mirror = new AgentMessage
        {
            ToHandle = runtime.Squad.PrincipalHandle,
            FromHandle = message.FromHandle,
            OnBehalfOfHandle = message.OnBehalfOfHandle,
            DeliverToHandle = message.DeliverToHandle,
            Channel = runtime.Squad.OrchestratorHandle,
            MessageType = messageType ?? message.MessageType ?? SurfaceSwarmMessageTypes.Chat,
            Message = message.Message,
            Kind = MessageKind.Response,
            DataType = message.DataType,
            Data = message.Data,
            Files = [.. message.Files],
            State = message.State is null ? new Dictionary<string, string>() : new Dictionary<string, string>(message.State),
            Args = message.Args is null ? new Dictionary<string, string>() : new Dictionary<string, string>(message.Args),
            TraceId = message.TraceId
        };

        Stamp(mirror);
        mirror.Args![SurfaceSwarmArgs.Mirror] = "true";
        if (!string.IsNullOrWhiteSpace(message.FromHandle))
        {
            mirror.Args[SurfaceSwarmArgs.OriginalFromHandle] = message.FromHandle;
        }

        if (!string.IsNullOrWhiteSpace(message.ToHandle))
        {
            mirror.Args[SurfaceSwarmArgs.OriginalToHandle] = message.ToHandle;
        }

        return agentHost.SendMessage(mirror);
    }

    public void Stamp(AgentMessage message)
    {
        message.Args ??= new Dictionary<string, string>();
        message.Args[SurfaceSwarmArgs.SquadHandle] = runtime.Squad.OrchestratorHandle;
        message.Args[SurfaceSwarmArgs.SquadName] = runtime.Squad.Name;
        message.Args[SurfaceSwarmArgs.SquadSlug] = runtime.Squad.Slug;
        message.Channel ??= runtime.Squad.OrchestratorHandle;
    }
}
