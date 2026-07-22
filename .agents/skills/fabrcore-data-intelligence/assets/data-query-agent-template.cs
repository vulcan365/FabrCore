using FabrCore.Services.DataIntelligence.Agents;
using FabrCore.Services.DataIntelligence.Context;
using FabrCore.Services.DataIntelligence.Factory;
using FabrCore.Services.DataIntelligence.Projection;
using FabrCore.Services.DataIntelligence.Query;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Data query agent template with schema-aware system prompt injection
/// and budget-aware projected query tools.
/// </summary>
[AgentAlias("data-query-agent")]
public class DataQueryAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public DataQueryAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // 1. Inject schema + filter awareness into system prompt
        var diContext = serviceProvider.GetRequiredService<DataIntelligenceContext>();
        config.SystemPrompt += "\n\n" + diContext.FormatForSystemPrompt();

        // 2. Guard against poisoning planner consultations with self-referential
        //    "missing tools" claims. See DataSmeSystemPromptGuard for rationale.
        config.SystemPrompt += "\n\n" + DataSmeSystemPromptGuard.Text;

        // 3. Set up tools (your query plugin tools)
        var tools = await ResolveConfiguredToolsAsync();

        // 4. Create LLM agent
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);
        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }
        return response;
    }
}
