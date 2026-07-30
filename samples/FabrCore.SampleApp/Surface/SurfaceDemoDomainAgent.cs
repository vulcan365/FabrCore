using System.ComponentModel;
using System.Text;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.SampleApp.Surface;

[AgentAlias(Alias)]
[Description("LLM-backed fake domain agent for the SurfaceApp squad-of-squads demo.")]
[FabrCoreCapabilities("Uses demo domain tools to read and update in-memory fake domain records, decisions, risk notes, and handoff suggestions for Surface squad orchestration demos.")]
[FabrCoreNote("Demo-only agent. Tool calls use in-memory fake data but are recorded as verifiable execution external effects when enabled.")]
public sealed class SurfaceDemoDomainAgent(
    AgentConfiguration config,
    IServiceProvider serviceProvider,
    IFabrCoreAgentHost fabrcoreAgentHost)
    : FabrCoreAgentProxy(config, serviceProvider, fabrcoreAgentHost)
{
    private const string DefaultPrompt = """
        You are a SurfaceApp demo leaf domain specialist.

        You are intentionally close to a production domain agent:
        - Use your configured demo domain tools before answering factual questions.
        - Treat tool responses as fake API/database data owned by this demo.
        - You may update demo records when the user asks for a note, status, or fake workflow change.
        - Make assumptions explicit and recommend handoffs when another branch should be involved.
        - Keep responses concise, grounded in tool results, and clear that the data is demo data.
        """;

    public const string Alias = "surface-demo-domain-agent";

    public const string DomainArg = "demo:Domain";

    public const string ProfileArg = "demo:Profile";

    public const string ResponsibilitiesArg = "demo:Responsibilities";

    public const string RecordsArg = "demo:Records";

    public const string DecisionsArg = "demo:Decisions";

    public const string HandoffsArg = "demo:Handoffs";

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
            response.Message = "Surface demo domain agent is not initialized.";
            return response;
        }

        var domain = Read(config.Args ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), DomainArg, "demo domain");
        SetStatusMessage($"Checking {domain} demo data...");
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

        var args = configuration.Args ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var domain = Read(args, DomainArg, "Demo Operations");
        var profile = Read(args, ProfileArg, configuration.Description ?? configuration.Handle ?? "Domain Specialist");
        var responsibilities = Read(args, ResponsibilitiesArg, "Triage the request; summarize fake operational facts; recommend the next squad handoff");
        var handoffs = Read(args, HandoffsArg, "Escalate cross-domain questions to Assistant");

        return $"""
            {DefaultPrompt}

            Specialist profile: {profile}
            Domain: {domain}
            Responsibilities: {responsibilities}
            Default handoff guidance: {handoffs}

            Required workflow:
            1. Call GetDomainBrief for orientation, or SearchDomainRecords/ListDomainRecords for record-specific questions.
            2. Call UpdateDomainRecord or AddDomainRecord when the user asks to mutate demo state.
            3. Answer from the tool results. Do not invent record IDs, statuses, or customer facts that were not returned by a tool.
            """;
    }

    public static string BuildResponse(AgentConfiguration configuration, string? userRequest)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var args = configuration.Args ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var domain = Read(args, DomainArg, "Demo Operations");
        var profile = Read(args, ProfileArg, configuration.Description ?? configuration.Handle ?? "Domain Specialist");
        var responsibilities = Split(Read(args, ResponsibilitiesArg, "Triage the request; summarize fake operational facts; recommend the next squad handoff"));
        var records = Split(Read(args, RecordsArg, "DEMO-001: Sample record awaiting review; DEMO-002: Sample record in progress"));
        var decisions = Split(Read(args, DecisionsArg, "Use the most specific branch specialist; flag assumptions before handoff"));
        var handoffs = Split(Read(args, HandoffsArg, "Escalate cross-domain questions to Assistant"));

        var builder = new StringBuilder();
        builder.AppendLine($"## {profile}");
        builder.AppendLine();
        builder.AppendLine($"**Domain:** {domain}");
        builder.AppendLine($"**Request reviewed:** {BlankToFallback(userRequest, "No user request text supplied.")}");
        builder.AppendLine();
        AppendList(builder, "Responsibilities", responsibilities);
        AppendList(builder, "Fake records reviewed", records);
        AppendList(builder, "Evidence-friendly decisions", decisions);
        AppendList(builder, "Suggested handoffs", handoffs);
        builder.AppendLine();
        builder.AppendLine("This is deterministic demo output for SurfaceApp verifiable execution testing.");
        return builder.ToString().TrimEnd();
    }

    private static string Read(IReadOnlyDictionary<string, string> args, string key, string fallback)
        => args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static IReadOnlyList<string> Split(string value)
        => value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.AppendLine($"**{title}:**");
        foreach (var item in items)
        {
            builder.AppendLine($"- {item}");
        }

        builder.AppendLine();
    }

    private static string BlankToFallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
