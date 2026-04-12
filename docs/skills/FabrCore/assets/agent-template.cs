using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// {{AGENT_DESCRIPTION}}
/// </summary>
[AgentAlias("{{AGENT_ALIAS}}")]
[Description("{{AGENT_DESCRIPTION}}")]
[FabrCoreCapabilities("{{AGENT_CAPABILITIES}}")]
[FabrCoreNote("{{AGENT_NOTE}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // Resolve plugins, standalone tools, and MCP tools from config
        var tools = await ResolveConfiguredToolsAsync();

        var result = await CreateChatClientAgent(
            "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);

        await foreach (var update in _agent!.RunStreamingAsync(
            chatMessage, _session!))
        {
            response.Message += update.Text;
        }

        return response;
    }

    /// <summary>
    /// Called when a new message arrives while OnMessage is already running.
    /// Override for custom busy-state handling. Default returns a "busy" response.
    /// IMPORTANT: Do not mutate shared agent state here — OnMessage may be mid-execution.
    /// ActiveMessage returns the message currently being processed by the primary handler.
    /// </summary>
    public override Task<AgentMessage> OnMessageBusy(AgentMessage message)
    {
        // Example: acknowledge receipt with context about what's happening
        return Task.FromResult(new AgentMessage
        {
            ToHandle = message.FromHandle,
            FromHandle = config.Handle,
            OnBehalfOfHandle = message.OnBehalfOfHandle,
            Message = "I'm currently working on a request. I'll be available shortly.",
            MessageType = message.MessageType,
            Kind = MessageKind.Response,
            TraceId = message.TraceId
        });
    }

    public override Task OnEvent(EventMessage eventMessage)
    {
        return Task.CompletedTask;
    }
}

// Example AgentConfiguration:
// {
//   "Handle": "{{AGENT_ALIAS}}",
//   "AgentType": "{{AGENT_ALIAS}}",
//   "Models": "default",
//   "SystemPrompt": "{{AGENT_DESCRIPTION}}",
//   "Plugins": [],
//   "Tools": [],
//   "Args": {}
// }
