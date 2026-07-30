using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Swarm;

[AgentAlias(SurfaceSwarmAgentTypes.Planner)]
[Description("Built-in planner for Surface Swarm squads.")]
[FabrCoreCapabilities("Decomposes goals into a Swarm task ledger with dependencies, acceptance criteria, and executor assignments; consults subject-matter-expert squad members before finalizing plans and revises ledgers on replan requests.")]
[FabrCoreNote("Assigns tasks only to Executor-role members; SubjectMatterExpert members are consult-only.")]
public sealed class SurfaceSwarmPlannerAgent : FabrCoreAgentProxy
{
    private SurfaceSwarmSquadRuntime runtime = new();
    private SurfaceSwarmSquadConversationBus? bus;
    private SurfaceSwarmCapabilityRegistry? capabilityRegistry;
    private AIAgent? agent;
    private AgentSession? session;
    private int smeConsultationsRemaining;
    private readonly ILogger<SurfaceSwarmPlannerAgent> plannerLogger;

    public SurfaceSwarmPlannerAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
        plannerLogger = loggerFactory.CreateLogger<SurfaceSwarmPlannerAgent>();
    }

    public override async Task OnInitialize()
    {
        runtime = SurfaceSwarmSquadRuntime.FromConfiguration(config, fabrcoreAgentHost.GetHandle());
        bus = new SurfaceSwarmSquadConversationBus(fabrcoreAgentHost, runtime);
        capabilityRegistry = new SurfaceSwarmCapabilityRegistry(
            serviceProvider.GetService<IFabrCoreRegistry>(),
            fabrcoreAgentHost,
            plannerLogger);

        var tools = await ResolveConfiguredToolsAsync();
        tools.Add(AIFunctionFactory.Create(ConsultSubjectMatterExpertAsync));
        tools.Add(AIFunctionFactory.Create(ConsultAllSubjectMatterExpertsAsync));

        var result = await CreateChatClientAgent(
            BlankToDefault(config.Models),
            $"{fabrcoreAgentHost.GetHandle()}:planner",
            tools);
        agent = result.Agent;
        session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        response.MessageType = SurfaceSwarmMessageTypes.PlanningResponse;
        bus?.Stamp(response);

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            response.Message = "Send a goal for this Swarm squad to plan.";
            return response;
        }

        if (agent is null || session is null || bus is null || capabilityRegistry is null)
        {
            response.Message = "Swarm planner is not initialized.";
            return response;
        }

        var context = ReadPlanningContext(message);
        smeConsultationsRemaining = Math.Max(
            0,
            runtime.Squad.Budgets.MaxSmeConsultationsPerPlanningPass);

        SetStatusMessage("Building task ledger..");
        var cards = await capabilityRegistry.BuildCardsAsync(runtime.Squad);
        var executors = runtime.Squad.Agents
            .Where(candidate => candidate.Role == SurfaceSwarmSquadMemberRole.Executor)
            .ToList();

        if (executors.Count == 0)
        {
            response.Message = "Add at least one Executor-role member before planning.";
            return response;
        }

        var prompt = BuildPlanningPrompt(message.Message!, cards, context);
        var ledger = await DraftLedgerAsync(prompt, executors, context);
        SetStatusMessage(null);

        if (ledger is null)
        {
            response.Message = "I could not produce a valid task ledger for that goal. Try adding detail or more executor agents.";
            return response;
        }

        response.DataType = SurfaceSwarmDataTypes.TaskLedger;
        response.Data = Encoding.UTF8.GetBytes(SwarmJson.Serialize(ledger));
        response.Message = FormatLedgerSummary(ledger);
        return response;
    }

    [Description("Ask ONE named SubjectMatterExpert squad member a domain question before finalizing the plan. Use for ambiguity, domain constraints, or acceptance-criteria details.")]
    public async Task<string> ConsultSubjectMatterExpertAsync(
        [Description("The SubjectMatterExpert member name from the squad roster.")] string agentName,
        [Description("The domain question to ask.")] string question)
    {
        if (bus is null)
        {
            return "The Swarm conversation bus is not initialized.";
        }

        if (smeConsultationsRemaining <= 0)
        {
            return "SME consultation budget exhausted — finalize the plan with available information.";
        }

        var sme = runtime.FindAgent(agentName);
        if (sme is null || sme.Role != SurfaceSwarmSquadMemberRole.SubjectMatterExpert)
        {
            var smeNames = runtime.Squad.Agents
                .Where(candidate => candidate.Role == SurfaceSwarmSquadMemberRole.SubjectMatterExpert)
                .Select(candidate => candidate.Name);
            return $"'{agentName}' is not a SubjectMatterExpert member. Available SMEs: {string.Join(", ", smeNames)}.";
        }

        smeConsultationsRemaining--;
        var answer = await CreateConsultant().ConsultAsync(sme, question);
        return answer is null
            ? $"{sme.Name} could not help with that question."
            : $"{answer.SmeName}: {answer.Answer}";
    }

    [Description("Ask ALL SubjectMatterExpert squad members the same domain question in parallel and collect every answer. Use when multiple perspectives are valuable.")]
    public async Task<string> ConsultAllSubjectMatterExpertsAsync(
        [Description("The domain question to ask every SME.")] string question)
    {
        if (bus is null)
        {
            return "The Swarm conversation bus is not initialized.";
        }

        if (smeConsultationsRemaining <= 0)
        {
            return "SME consultation budget exhausted — finalize the plan with available information.";
        }

        smeConsultationsRemaining--;
        var answers = await CreateConsultant().ConsultAllAsync(question);
        return answers.Count == 0
            ? "No subject matter expert could help with that question."
            : string.Join(Environment.NewLine, answers.Select(answer => $"{answer.SmeName}: {answer.Answer}"));
    }

    private SurfaceSwarmSmeConsultant CreateConsultant()
        => new(
            bus!,
            runtime,
            fabrcoreAgentHost.GetHandle(),
            TimeSpan.FromSeconds(runtime.Squad.Budgets.SmeConsultationTimeoutSeconds),
            plannerLogger);

    private async Task<TaskLedger?> DraftLedgerAsync(
        string prompt,
        IReadOnlyList<SurfaceSwarmSquadAgent> executors,
        SwarmPlanningContext? context)
    {
        var draft = await RunDraftPassAsync(prompt);
        var errors = draft is null
            ? ["The planner output was not valid task-ledger JSON."]
            : SurfaceSwarmPlanValidation.ValidateDraft(draft, runtime.Squad.Agents);
        if (draft is not null && errors.Count == 0)
        {
            return SurfaceSwarmPlanValidation.ToLedger(draft, runtime.Squad.Agents, LastGoal, context?.PriorLedger);
        }

        plannerLogger.LogWarning(
            "Swarm planner draft invalid; retrying once - Handle: {Handle}, Errors: {Errors}",
            fabrcoreAgentHost.GetHandle(),
            string.Join("; ", errors));

        var corrective = $"""
            Your previous plan draft was rejected:
            {string.Join(Environment.NewLine, errors.Select(error => $"- {error}"))}

            Emit the corrected task ledger now as JSON only. Do not call any more tools.
            """;
        draft = await RunDraftPassAsync(corrective);
        if (draft is null)
        {
            return null;
        }

        errors = SurfaceSwarmPlanValidation.ValidateDraft(draft, runtime.Squad.Agents);
        return errors.Count == 0
            ? SurfaceSwarmPlanValidation.ToLedger(draft, runtime.Squad.Agents, LastGoal, context?.PriorLedger)
            : null;
    }

    private async Task<SwarmLedgerDraft?> RunDraftPassAsync(string prompt)
    {
        var result = await agent!.RunAsync(new ChatMessage(ChatRole.User, prompt), session!);
        var text = result.Messages.LastOrDefault()?.Text ?? string.Empty;
        return SwarmLedgerDraft.Parse(text);
    }

    private string LastGoal { get; set; } = string.Empty;

    private string BuildPlanningPrompt(
        string goal,
        IReadOnlyList<SurfaceSwarmCapabilityCard> cards,
        SwarmPlanningContext? context)
    {
        LastGoal = string.IsNullOrWhiteSpace(context?.PriorLedger?.Goal) ? goal : context!.PriorLedger!.Goal;

        var sb = new StringBuilder();
        sb.AppendLine($"You are planning for the Swarm squad \"{runtime.Squad.Name}\".");
        sb.AppendLine();

        if (context?.PriorLedger is not null)
        {
            sb.AppendLine("This is a REPLAN request. Keep completed work; replace or append the rest.");
            sb.AppendLine();
            sb.AppendLine("Prior ledger:");
            sb.AppendLine(SwarmJson.Serialize(context.PriorLedger));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(context.ProgressSummary))
            {
                sb.AppendLine("Progress so far:");
                sb.AppendLine(context.ProgressSummary);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(context.FailureSignal))
            {
                sb.AppendLine("Failure signal:");
                sb.AppendLine(context.FailureSignal);
                sb.AppendLine();
            }
        }

        sb.AppendLine("Goal:");
        sb.AppendLine(goal);
        sb.AppendLine();
        sb.AppendLine("Squad roster (grouped by role):");
        sb.AppendLine(SurfaceSwarmCapabilityRegistry.FormatForPrompt(cards));
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Assign tasks ONLY to Executor-role members.");
        sb.AppendLine("- SubjectMatterExpert members are consult-only: use the consult tools to resolve ambiguity, gather domain constraints, and sharpen acceptance criteria BEFORE finalizing. Never assign them tasks.");
        sb.AppendLine($"- You may make at most {smeConsultationsRemaining} SME consultations for this plan.");
        sb.AppendLine("- Every task needs objective, checkable acceptance criteria — a verifier will judge results against them literally.");
        sb.AppendLine("- Use dependsOn for ordering; independent tasks may run in parallel.");
        sb.AppendLine("- Keep the plan as small as the goal allows.");
        sb.AppendLine();
        sb.AppendLine("""When ready, respond with JSON only: {"facts":[],"hypotheses":[],"tasks":[{"id":"t1","title":"...","description":"...","dependsOn":[],"acceptanceCriteria":["..."],"assignedAgentName":"executor name","rationale":"..."}],"openQuestions":[]}""");
        return sb.ToString();
    }

    private static string FormatLedgerSummary(TaskLedger ledger)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task ledger (revision {ledger.Revision}) with {ledger.Tasks.Count} task{(ledger.Tasks.Count == 1 ? string.Empty : "s")}:");
        foreach (var task in ledger.Tasks)
        {
            var deps = task.DependsOn.Count > 0 ? $" (after {string.Join(", ", task.DependsOn)})" : string.Empty;
            sb.AppendLine($"- {task.Id}: {task.Title} → {task.AssignedAgentName}{deps}");
        }

        return sb.ToString().TrimEnd();
    }

    private static SwarmPlanningContext? ReadPlanningContext(AgentMessage message)
    {
        if (message.Data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SwarmPlanningContext>(
                Encoding.UTF8.GetString(message.Data),
                SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
}
