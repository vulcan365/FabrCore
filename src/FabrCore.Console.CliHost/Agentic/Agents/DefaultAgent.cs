using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Console.CliHost.Agentic.Agents;

[AgentAlias("DefaultAgent")]
public class DefaultAgent : FabrCoreAgentProxy
{
    private ChatClientAgentResult? _chatAgent;

    public DefaultAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrAgentHost)
        : base(config, serviceProvider, fabrAgentHost) { }

    public override async Task OnInitialize()
    {
        var modelConfig = config.Models ?? "default";
        var threadId = $"{config.Handle}-thread";

        _chatAgent = await CreateChatClientAgent(modelConfig, threadId);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        var chatMessage = new ChatMessage(ChatRole.User, message.Message);
        var result = await _chatAgent!.Agent.RunAsync(chatMessage, _chatAgent.Session);

        response.Message = string.Join("", result.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Select(t => t.Text));

        return response;
    }
}
