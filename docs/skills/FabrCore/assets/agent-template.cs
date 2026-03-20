using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

/// <summary>
/// {{AGENT_DESCRIPTION}}
/// </summary>
[AgentAlias("{{AGENT_ALIAS}}")]
public class {{AGENT_NAME}} : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public {{AGENT_NAME}}(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrAgentHost)
        : base(config, serviceProvider, fabrAgentHost) { }

    public override async Task OnInitialize()
    {
        // Resolve plugins, standalone tools, and MCP tools from config
        var tools = await ResolveConfiguredToolsAsync();

        var result = await CreateChatClientAgent(
            "default",
            threadId: config.Handle ?? fabrAgentHost.GetHandle(),
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
