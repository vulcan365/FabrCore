using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;

[AgentAlias("scripting-assistant")]
public sealed class ScriptingAgent(
    AgentConfiguration config,
    IServiceProvider services,
    IFabrCoreAgentHost host) : FabrCoreAgentProxy(config, services, host)
{
    private AIAgent agent = null!;
    private AgentSession session = null!;

    public override async Task OnInitialize()
    {
        if (!config.Plugins.Contains("reporting-scripts"))
            config.Plugins.Add("reporting-scripts");
        var tools = await ResolveConfiguredToolsAsync();
        var created = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        agent = created.Agent;
        session = created.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var result = await agent.RunAsync(message.Message ?? string.Empty, session);
        var response = message.Response();
        response.Message = result.Text;
        return response;
    }
}
