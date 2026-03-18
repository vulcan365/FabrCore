using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

/// <summary>
/// {{AGENT_DESCRIPTION}}
/// </summary>
[AgentAlias("{{AGENT_ALIAS}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private ChatClientAgent? _agent;
    private AgentThread? _thread;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrAgentHost)
        : base(config, serviceProvider, fabrAgentHost) { }

    public override async Task OnInitialize()
    {
        var result = await CreateChatClientAgent("default");
        _agent = result.Agent;
        _thread = result.Thread;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);

        await foreach (var msg in _agent!.InvokeStreamingAsync(
            [chatMessage], _thread!))
        {
            response.Message += msg.Text;
        }

        return response;
    }

    public override Task OnEvent(AgentMessage eventMessage)
    {
        return Task.CompletedTask;
    }
}

// Example AgentConfiguration:
// {
//   "Handle": "{{AGENT_ALIAS}}",
//   "AgentType": "{{AGENT_ALIAS}}",
//   "Models": ["default"],
//   "SystemPrompt": "{{AGENT_DESCRIPTION}}",
//   "Plugins": [],
//   "Tools": [],
//   "Args": {}
// }
