using FabrCore.Core;
using FabrCore.Sdk;

namespace FabrCore.Surface.Ai.Squads;

public sealed class SurfaceSquadConversationBus
{
    private readonly IFabrCoreAgentHost agentHost;
    private readonly SurfaceSquadRuntime runtime;

    public SurfaceSquadConversationBus(IFabrCoreAgentHost agentHost, SurfaceSquadRuntime runtime)
    {
        this.agentHost = agentHost;
        this.runtime = runtime;
    }

    public async Task<AgentMessage> SendAndReceiveAsync(AgentMessage request)
    {
        Stamp(request);
        await MirrorAsync(request, SurfaceSquadMessageTypes.AgentRequest);

        var response = await agentHost.SendAndReceiveMessage(request);
        Stamp(response);
        response.Args![SurfaceSquadArgs.SquadHandle] = runtime.Squad.OrchestratorHandle;
        await MirrorAsync(response, SurfaceSquadMessageTypes.AgentResponse);
        return response;
    }

    public async Task SendAsync(AgentMessage request)
    {
        Stamp(request);
        await MirrorAsync(request, request.MessageType ?? SurfaceSquadMessageTypes.Chat);
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
            MessageType = messageType ?? message.MessageType ?? SurfaceSquadMessageTypes.Chat,
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
        mirror.Args![SurfaceSquadArgs.Mirror] = "true";
        if (!string.IsNullOrWhiteSpace(message.FromHandle))
        {
            mirror.Args[SurfaceSquadArgs.OriginalFromHandle] = message.FromHandle;
        }

        if (!string.IsNullOrWhiteSpace(message.ToHandle))
        {
            mirror.Args[SurfaceSquadArgs.OriginalToHandle] = message.ToHandle;
        }

        return agentHost.SendMessage(mirror);
    }

    public void Stamp(AgentMessage message)
    {
        message.Args ??= new Dictionary<string, string>();
        message.Args[SurfaceSquadArgs.SquadHandle] = runtime.Squad.OrchestratorHandle;
        message.Args[SurfaceSquadArgs.SquadName] = runtime.Squad.Name;
        message.Args[SurfaceSquadArgs.SquadSlug] = runtime.Squad.Slug;
        message.Channel ??= runtime.Squad.OrchestratorHandle;
    }
}
