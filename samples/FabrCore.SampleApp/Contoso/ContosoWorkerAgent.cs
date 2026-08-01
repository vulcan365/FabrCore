using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.SampleApp.Contoso;

[AgentAlias(Alias)]
[Description("LLM-backed Contoso Bike Shop specialist for the Swarm demo squad.")]
[FabrCoreCapabilities("Executes delegated Contoso Bike Shop tasks (CRM, HR, marketing) using tracked in-memory demo data plugins configured per persona.")]
[FabrCoreNote("Demo-only agent. Tool data is fake but really tracked in memory, so multi-step swarm tasks observe consistent state.")]
public sealed class ContosoWorkerAgent(
    AgentConfiguration config,
    IServiceProvider serviceProvider,
    IFabrCoreAgentHost fabrcoreAgentHost)
    : FabrCoreAgentProxy(config, serviceProvider, fabrcoreAgentHost)
{
    public const string Alias = "contoso-worker-agent";

    public const string PersonaArg = "contoso:Persona";

    public const string FocusArg = "contoso:Focus";

    public const string PlaybookArg = "contoso:Playbook";

    private const string DefaultPrompt = """
        You are a specialist employee at Contoso Bike Shop, a friendly neighborhood bicycle store, working inside a FabrCore Swarm squad.

        Ground rules:
        - Always call your configured Contoso data tools before stating facts. Never invent record IDs, people, or numbers that a tool did not return.
        - The data is demo data, but it is really tracked in memory: when you add or update a record, other squad members will see your change.
        - When a task asks you to mutate data (add a customer, hire an employee, create a campaign, update a status), actually call the mutation tool and report the resulting record ID.
        - Complete the specific task you were given; do not expand scope. If the task needs another domain (CRM vs HR vs Marketing), finish your part and state clearly what the next specialist needs.
        - Return concise, structured results: lead with the outcome, list the record IDs you touched, and include the key facts the orchestrator needs to synthesize an answer.
        """;

    private AIAgent? agent;
    private AgentSession? session;

    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();

        config.SystemPrompt = string.IsNullOrWhiteSpace(config.SystemPrompt)
            ? BuildSystemPrompt(config)
            : $"{config.SystemPrompt}\n\n{BuildSystemPrompt(config)}";

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
        {
            response.Message = "Contoso worker agent is not initialized.";
            return response;
        }

        var persona = Read(config.Args, PersonaArg, "Contoso specialist");
        SetStatusMessage($"{persona} is working...");
        try
        {
            await foreach (var update in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, message.Message), session))
            {
                response.Message += update.Text;
            }
        }
        finally
        {
            SetStatusMessage(null);
        }

        return response;
    }

    public override Task OnEvent(EventMessage eventMessage) => Task.CompletedTask;

    public static string BuildSystemPrompt(AgentConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var persona = Read(configuration.Args, PersonaArg, configuration.Description ?? configuration.Handle ?? "Contoso Specialist");
        var focus = Read(configuration.Args, FocusArg, "Handle delegated Contoso Bike Shop tasks with the configured data tools.");
        var playbook = Read(configuration.Args, PlaybookArg, "Finish your task, report record IDs, and recommend the next specialist when work crosses domains.");

        return $"""
            {DefaultPrompt}

            Your persona: {persona}
            Your focus: {focus}
            Your playbook: {playbook}
            """;
    }

    private static string Read(IReadOnlyDictionary<string, string>? args, string key, string fallback)
        => args is not null && args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
}
