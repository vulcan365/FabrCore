using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;

namespace FabrCore.SampleApp.Scripting;

[AgentAlias(Alias)]
[Description("Scripting demo: calculate fictional sales totals and grouped revenue with C#.")]
[FabrCoreCapabilities("Uses a developer-configured C# plugin to analyze sample sales data in a separate worker process.")]
public sealed class ScriptingDemoAgent(
    AgentConfiguration config,
    IServiceProvider services,
    IFabrCoreAgentHost host) : FabrCoreAgentProxy(config, services, host)
{
    public const string Alias = "scripting-demo-agent";

    private AIAgent? agent;
    private AgentSession? session;

    public override async Task OnInitialize()
    {
        if (!config.Plugins.Contains(SalesScriptingPlugin.Alias))
            config.Plugins.Add(SalesScriptingPlugin.Alias);

        var tools = await ResolveConfiguredToolsAsync();
        config.SystemPrompt = """
            You demonstrate C# scripting with fictional sales data. For calculations, first call
            GetScriptingEnvironment and GetDemoSales, then execute C# with the returned data as input.
            Use the developer's package guidance and decimal arithmetic. Never invent totals or
            claim execution succeeded when the tool reports an error. Explain results briefly,
            including the number of orders and currency. Do not create artifacts unless requested.
            If a report is requested, summarize its contents in chat and name the returned artifact;
            do not invent a download URL. Files and variables do not persist between executions.
            This is a trusted local demo, not an environment for untrusted code or real customer data.
            """;

        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        agent = result.Agent;
        session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        if (agent is null || session is null)
            throw new InvalidOperationException("The scripting demo agent is not initialized.");

        SetStatusMessage("Running the scripting demo...");
        try
        {
            var result = await agent.RunAsync(message.Message ?? "", session);
            response.Message = result.Text;
            return response;
        }
        finally
        {
            SetStatusMessage(null);
        }
    }
}
