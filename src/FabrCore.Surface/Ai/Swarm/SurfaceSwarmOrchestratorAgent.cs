using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Swarm;

[AgentAlias(SurfaceSwarmAgentTypes.Orchestrator)]
[Description("Built-in orchestrator for Surface Swarm squads.")]
[FabrCoreCapabilities("User-facing entry point for a Swarm squad: triages requests with a fast-model classifier, answers simple asks directly, routes complex work through the planner and supervisor harness, gates high-risk plans on approval, and synthesizes final squad answers.")]
[FabrCoreNote("Reply 'approve' or 'reject' when a plan is awaiting approval.")]
public sealed class SurfaceSwarmOrchestratorAgent : FabrCoreAgentProxy
{
    private const string PendingRunStateKey = "orch-pending-run";
    private const string ActiveRunStateKey = "orch-active-run";

    private SurfaceSwarmSquadRuntime runtime = new();
    private SurfaceSwarmSquadConversationBus? bus;
    private IChatClient? classifierClient;
    private AIAgent? synthesizer;
    private AgentSession? synthesizerSession;
    private readonly ILogger<SurfaceSwarmOrchestratorAgent> orchestratorLogger;

    public SurfaceSwarmOrchestratorAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
        orchestratorLogger = loggerFactory.CreateLogger<SurfaceSwarmOrchestratorAgent>();
    }

    public override async Task OnInitialize()
    {
        runtime = SurfaceSwarmSquadRuntime.FromConfiguration(config, fabrcoreAgentHost.GetHandle());
        bus = new SurfaceSwarmSquadConversationBus(fabrcoreAgentHost, runtime);

        classifierClient = await GetChatClient(BlankToDefault(runtime.Squad.FastModel));

        var tools = await ResolveConfiguredToolsAsync();
        tools.Add(AIFunctionFactory.Create(ListAgents));
        tools.Add(AIFunctionFactory.Create(AskAgentAsync));
        var result = await CreateChatClientAgent(
            BlankToDefault(config.Models),
            $"{fabrcoreAgentHost.GetHandle()}:orchestrator",
            tools);
        synthesizer = result.Agent;
        synthesizerSession = result.Session;
    }

    [Description("Lists the members of this Swarm squad with their roles and descriptions.")]
    public string ListAgents()
    {
        if (runtime.Squad.Agents.Count == 0)
        {
            return "This squad has no member agents yet. Members can be added through the squad settings.";
        }

        return string.Join(Environment.NewLine, runtime.Squad.Agents.Select(agent =>
            $"- {agent.Name}: type={agent.AgentType}, role={agent.Role}, handle={agent.Handle}{(string.IsNullOrWhiteSpace(agent.Description) ? string.Empty : $", description={agent.Description}")}"));
    }

    [Description("Asks one squad member a question or gives it a small task directly. Use the member name from list_agents. For multi-step work, answer that the squad will plan and execute it instead.")]
    public async Task<string> AskAgentAsync(
        [Description("The squad member name to ask, for example 'Surface' or 'CRM Records'.")] string agentName,
        [Description("The question or small task to send to the member.")] string question)
    {
        if (bus is null)
        {
            return "The Swarm conversation bus is not initialized.";
        }

        var target = runtime.FindAgent(agentName);
        if (target is null)
        {
            return $"No squad member named '{agentName}' exists. Available members: {string.Join(", ", runtime.Squad.Agents.Select(agent => agent.Name))}.";
        }

        var response = await bus.SendAndReceiveAsync(new AgentMessage
        {
            FromHandle = fabrcoreAgentHost.GetHandle(),
            ToHandle = target.Handle,
            MessageType = SurfaceSwarmMessageTypes.TaskDispatch,
            Kind = MessageKind.Request,
            Message = question,
            Args = new Dictionary<string, string>
            {
                [SurfaceSwarmArgs.AgentName] = target.Name
            }
        });

        if (SurfaceSwarmSquadConversationBus.IsAdaptiveCardRender(response))
        {
            return $"{response.Message}\n\n({target.Name} rendered an interactive card with the results directly in the chat — do not restate its contents; refer the user to the card.)";
        }

        return response.Message ?? string.Empty;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        Stamp(response);

        if (message.MessageType == SurfaceSwarmMessageTypes.Final)
        {
            return await HandleFinalReportAsync(message, response);
        }

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            response.Message = "Send a goal for this Swarm squad.";
            return response;
        }

        if (classifierClient is null || synthesizer is null || synthesizerSession is null || bus is null)
        {
            response.Message = "Swarm orchestrator is not initialized.";
            return response;
        }

        var pending = await TryGetStateAsync<SwarmExecutePayload>(PendingRunStateKey);
        if (pending.Succeeded && pending.Value is not null)
        {
            return await HandlePendingApprovalAsync(message, response, pending.Value);
        }

        SetStatusMessage("Triaging request..");
        var policy = await TriageAsync(message.Message!);
        SetStatusMessage(null);

        if (!policy.NeedsPlan)
        {
            SetStatusMessage("Answering directly..");
            var directPrompt = $"""
                You are coordinating the Swarm squad "{runtime.Squad.Name}".

                Squad members:
                {ListAgents()}

                Use ask_agent when a listed member should answer a domain question or do a small task.
                Answer the user directly when no member is needed.

                User request:
                {message.Message}
                """;
            var direct = await synthesizer.RunAsync(
                new ChatMessage(ChatRole.User, directPrompt),
                synthesizerSession);
            SetStatusMessage(null);
            response.Message = direct.Messages.LastOrDefault()?.Text ?? string.Empty;
            return response;
        }

        return await PlanAndExecuteAsync(message, response, policy);
    }

    private async Task<ExecutionPolicy> TriageAsync(string goal)
    {
        var prompt = $$"""
            Triage this request for the Swarm squad "{{runtime.Squad.Name}}".
            Squad members: {{FormatRoster()}}

            Request:
            {{goal}}

            Decide:
            - mode: "direct" when the orchestrator can answer alone (questions, chit-chat, single trivial lookups); "plan" when squad members must do multi-step work.
            - riskLevel: low|medium|high (high = external side effects, destructive actions, or costly work).
            - approvalRequired: true when a human should sign off on the plan before execution.
            - maxConcurrency: how many tasks may run in parallel.
            - verificationDepth: none|basic|strict.
            - replanThreshold: task failures tolerated before replanning.
            - workBrief: restate the goal preserving user intent, constraints, and success criteria.

            Return JSON: {"mode":"direct|plan","riskLevel":"low","approvalRequired":false,"maxConcurrency":1,"verificationDepth":"basic","replanThreshold":1,"workBrief":"...","directAnswerHint":null}
            """;

        try
        {
            var chatOptions = new ChatOptions
            {
                ResponseFormat = SwarmSchema.For<SwarmTriageResult>(
                    "SwarmTriageResult",
                    "Triage classification for a Swarm squad request")
            };
            var result = await classifierClient!.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                chatOptions);
            var triage = SwarmTriageResult.Parse(result.Text ?? string.Empty);
            if (triage is not null)
            {
                if (string.IsNullOrWhiteSpace(triage.WorkBrief))
                {
                    triage.WorkBrief = goal;
                }

                return SurfaceSwarmBudgetGuard.ClampTriage(triage, runtime.Squad.Budgets);
            }

            orchestratorLogger.LogWarning(
                "Swarm triage output could not be parsed; defaulting to plan mode - Handle: {Handle}",
                fabrcoreAgentHost.GetHandle());
        }
        catch (Exception ex)
        {
            orchestratorLogger.LogWarning(
                ex,
                "Swarm triage failed; defaulting to plan mode - Handle: {Handle}",
                fabrcoreAgentHost.GetHandle());
        }

        return SurfaceSwarmBudgetGuard.ClampTriage(
            new SwarmTriageResult { Mode = "plan", WorkBrief = goal },
            runtime.Squad.Budgets);
    }

    private async Task<AgentMessage> PlanAndExecuteAsync(
        AgentMessage message,
        AgentMessage response,
        ExecutionPolicy policy)
    {
        SetStatusMessage("Planning..");
        var planningContext = new SwarmPlanningContext
        {
            Policy = policy,
            Budgets = runtime.Squad.Budgets
        };
        var plannerResponse = await bus!.SendAndReceiveAsync(new AgentMessage
        {
            FromHandle = fabrcoreAgentHost.GetHandle(),
            ToHandle = runtime.Squad.PlannerHandle,
            MessageType = SurfaceSwarmMessageTypes.PlanningRequest,
            Message = message.Message,
            Kind = MessageKind.Request,
            DataType = SurfaceSwarmDataTypes.PlanningContext,
            Data = Encoding.UTF8.GetBytes(SwarmJson.Serialize(planningContext))
        });
        SetStatusMessage(null);

        var ledger = ReadLedger(plannerResponse);
        if (ledger is null || ledger.Tasks.Count == 0)
        {
            response.Message = plannerResponse.Message
                ?? "The planner could not produce a task ledger for that goal.";
            return response;
        }

        var payload = new SwarmExecutePayload
        {
            RunId = Guid.NewGuid().ToString("N")[..8],
            Ledger = ledger,
            Policy = policy,
            Budgets = runtime.Squad.Budgets,
            CallerHandle = fabrcoreAgentHost.GetHandle()
        };

        if (policy.ApprovalRequired)
        {
            SetState(PendingRunStateKey, payload);
            await FlushStateAsync();

            var approval = new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = runtime.Squad.PrincipalHandle,
                MessageType = SurfaceSwarmMessageTypes.ApprovalRequest,
                Kind = MessageKind.Response,
                Message = $"{plannerResponse.Message}\n\nReply 'approve' to execute this plan or 'reject' to cancel.",
                Args = new Dictionary<string, string>
                {
                    [SurfaceSwarmArgs.PendingApproval] = "true",
                    [SurfaceSwarmArgs.RunId] = payload.RunId
                }
            };
            await bus.MirrorAsync(approval, SurfaceSwarmMessageTypes.ApprovalRequest);

            response.Message = $"{plannerResponse.Message}\n\nThis plan requires approval. Reply 'approve' to execute or 'reject' to cancel.";
            return response;
        }

        return await StartRunAsync(payload, response, plannerResponse.Message);
    }

    private async Task<AgentMessage> HandlePendingApprovalAsync(
        AgentMessage message,
        AgentMessage response,
        SwarmExecutePayload pending)
    {
        var text = message.Message!.Trim();
        if (IsApproval(text))
        {
            RemoveState(PendingRunStateKey);
            await FlushStateAsync();
            return await StartRunAsync(pending, response, planSummary: null);
        }

        if (IsRejection(text))
        {
            RemoveState(PendingRunStateKey);
            await FlushStateAsync();
            response.Message = $"Plan {pending.RunId} was rejected and discarded. Send a new goal when ready.";
            return response;
        }

        response.Message = $"A plan ({pending.RunId}) for \"{Truncate(pending.Ledger.Goal, 120)}\" is awaiting approval. Reply 'approve' to execute it or 'reject' to cancel before sending new work.";
        return response;
    }

    private async Task<AgentMessage> StartRunAsync(
        SwarmExecutePayload payload,
        AgentMessage response,
        string? planSummary)
    {
        SetStatusMessage("Handing off to supervisor..");
        var accepted = await bus!.SendAndReceiveAsync(new AgentMessage
        {
            FromHandle = fabrcoreAgentHost.GetHandle(),
            ToHandle = runtime.Squad.SupervisorHandle,
            MessageType = SurfaceSwarmMessageTypes.ExecuteRequest,
            Kind = MessageKind.Request,
            Message = $"Execute run {payload.RunId}: {payload.Ledger.Goal}",
            DataType = SurfaceSwarmDataTypes.Execute,
            Data = Encoding.UTF8.GetBytes(SwarmJson.Serialize(payload))
        });
        SetStatusMessage(null);

        if (accepted.MessageType != SurfaceSwarmMessageTypes.ExecuteAccepted)
        {
            response.Message = accepted.Message ?? "The supervisor could not accept the run.";
            return response;
        }

        SetState(ActiveRunStateKey, payload);
        await FlushStateAsync();

        var summary = string.IsNullOrWhiteSpace(planSummary) ? string.Empty : $"{planSummary}\n\n";
        response.Message = $"{summary}The squad is working on it (run {payload.RunId}, {payload.Ledger.Tasks.Count} task{(payload.Ledger.Tasks.Count == 1 ? string.Empty : "s")}). Progress will appear here.";
        return response;
    }

    private async Task<AgentMessage> HandleFinalReportAsync(AgentMessage message, AgentMessage response)
    {
        var report = ReadFinalReport(message);
        if (report is null)
        {
            response.Message = "Received an unreadable final report.";
            return response;
        }

        RemoveState(ActiveRunStateKey);
        await FlushStateAsync();

        SetStatusMessage("Synthesizing final answer..");
        var final = await SynthesizeFinalAsync(report);
        SetStatusMessage(null);

        if (bus is not null)
        {
            await bus.MirrorAsync(new AgentMessage
            {
                FromHandle = fabrcoreAgentHost.GetHandle(),
                ToHandle = runtime.Squad.PrincipalHandle,
                MessageType = SurfaceSwarmMessageTypes.Chat,
                Kind = MessageKind.Response,
                Message = final
            }, SurfaceSwarmMessageTypes.Chat);
        }

        response.Message = final;
        return response;
    }

    private async Task<string> SynthesizeFinalAsync(SwarmFinalReport report)
    {
        var tasks = string.Join(Environment.NewLine, report.Tasks.Select(task =>
            $"- [{task.Status}] {task.TaskId} {task.Title}: {task.ResultSummary ?? task.FailureReason ?? "(no summary)"}"));
        var escalation = string.IsNullOrWhiteSpace(report.EscalationNote)
            ? string.Empty
            : $"\nEscalation note:\n{report.EscalationNote}\n";

        var prompt = $"""
            Write the final user-facing response for Swarm run {report.RunId} ({report.Outcome}).
            Be concise, lead with the outcome, and mention blockers plainly if the goal was not fully satisfied.

            Goal:
            {report.Goal}

            Task results:
            {tasks}
            {escalation}
            """;

        try
        {
            var result = await synthesizer!.RunAsync(
                new ChatMessage(ChatRole.User, prompt),
                synthesizerSession!);
            var text = result.Messages.LastOrDefault()?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text!;
            }
        }
        catch (Exception ex)
        {
            orchestratorLogger.LogWarning(
                ex,
                "Swarm final synthesis failed; falling back to raw report - Handle: {Handle}, RunId: {RunId}",
                fabrcoreAgentHost.GetHandle(),
                report.RunId);
        }

        return $"Run {report.RunId} finished with outcome '{report.Outcome}'.\n{tasks}{escalation}";
    }

    private static TaskLedger? ReadLedger(AgentMessage message)
    {
        if (message.Data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TaskLedger>(
                Encoding.UTF8.GetString(message.Data),
                SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SwarmFinalReport? ReadFinalReport(AgentMessage message)
    {
        if (message.Data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SwarmFinalReport>(
                Encoding.UTF8.GetString(message.Data),
                SurfaceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsApproval(string text)
        => text.Equals("approve", StringComparison.OrdinalIgnoreCase)
            || text.Equals("approved", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("go", StringComparison.OrdinalIgnoreCase);

    private static bool IsRejection(string text)
        => text.Equals("reject", StringComparison.OrdinalIgnoreCase)
            || text.Equals("rejected", StringComparison.OrdinalIgnoreCase)
            || text.Equals("no", StringComparison.OrdinalIgnoreCase)
            || text.Equals("cancel", StringComparison.OrdinalIgnoreCase);

    private string FormatRoster()
        => runtime.Squad.Agents.Count == 0
            ? "(none)"
            : string.Join("; ", runtime.Squad.Agents.Select(agent => $"{agent.Name} ({agent.Role})"));

    private void Stamp(AgentMessage message)
    {
        message.MessageType ??= SurfaceSwarmMessageTypes.Chat;
        bus?.Stamp(message);
    }

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }
}
