#pragma warning disable MAAI001 // Harness providers (LoopAgent, BackgroundAgentsProvider, loop evaluators) are for evaluation purposes only and may change.
using System.ComponentModel;
using System.Text;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Squads;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.Ai.Tasks;

/// <summary>
/// Coordinator for Surface Task squads, composed from the Microsoft Agent Framework harness primitives.
/// </summary>
/// <remarks>
/// <para>
/// The model owns the plan. <see cref="TodoProvider"/> supplies <c>todos_*</c> tools backed by the session
/// state bag; <see cref="BackgroundAgentsProvider"/> supplies <c>background_agents_*</c> tools over the
/// squad's registry-defined members; <see cref="LoopAgent"/> re-invokes until no todos and no delegations
/// remain outstanding. Every host/model contract is a typed tool call, so there is no JSON scraping,
/// response envelope, sentinel token, or timer trampoline.
/// </para>
/// <para>
/// A run completes inside a single grain turn. Harness provider state lives in the
/// <see cref="AgentSession"/> state bag, which FabrCore does not persist, so todos do not carry across
/// user turns by design.
/// </para>
/// </remarks>
[AgentAlias(SurfaceTaskAgentTypes.TaskRunner)]
[Description("Built-in task coordinator for Surface Task squads.")]
[FabrCoreCapabilities("Breaks a goal into tracked todos, delegates work concurrently to configured squad executors, consults subject matter experts, and reports a consolidated result into the Surface squad.")]
public sealed class SurfaceTaskHarnessAgent : FabrCoreAgentProxy
{
    private const string ThreadId = "main";

    private readonly ILogger<SurfaceTaskHarnessAgent> taskLogger;

    private SurfaceSquadRuntime runtime = new();
    private SurfaceSquadConversationBus? bus;
    private IFabrCoreRegistry? registry;
    private AIAgent? harness;
    private AgentSession? session;
    private TodoProvider? todoProvider;
    private BackgroundAgentsProvider? backgroundAgents;
    private List<SurfaceSquadMemberAgent> delegates = [];
    private List<string> excludedMembers = [];

    public SurfaceTaskHarnessAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
        taskLogger = loggerFactory.CreateLogger<SurfaceTaskHarnessAgent>();
    }

    public override async Task OnInitialize()
    {
        var handle = fabrcoreAgentHost.GetHandle();

        runtime = SurfaceSquadRuntime.FromConfiguration(config, handle);
        bus = new SurfaceSquadConversationBus(fabrcoreAgentHost, runtime);
        registry = serviceProvider.GetService<IFabrCoreRegistry>();

        var options = runtime.Squad.TaskOptions;
        var model = BlankToDefault(options.WorkerModelName);

        taskLogger.LogInformation(
            "Surface Task harness initializing - Handle: {Handle}, Squad: {SquadName}, SquadHandle: {SquadHandle}, Members: {MemberCount}, Model: {Model}",
            handle,
            runtime.Squad.Name,
            runtime.Squad.OrchestratorHandle,
            runtime.Squad.Agents.Count,
            model);

        var capabilities = await SurfaceSquadCapabilityLoader.BuildAsync(
            runtime.Squad,
            fabrcoreAgentHost,
            registry,
            includeRoleNote: true,
            taskLogger);

        delegates = SurfaceSquadMemberAgent.BuildAgents(
            runtime.Squad,
            capabilities,
            bus,
            fabrcoreAgentHost,
            options.ClientAgentOverlay,
            TimeSpan.FromSeconds(Math.Max(1, options.DelegationTimeoutSeconds)),
            out excludedMembers);

        if (excludedMembers.Count > 0)
        {
            taskLogger.LogWarning(
                "Surface Task harness excluded unavailable squad members - Handle: {Handle}, Excluded: {Excluded}",
                handle,
                string.Join("; ", excludedMembers));
        }

        if (delegates.Count == 0)
        {
            taskLogger.LogWarning(
                "Surface Task harness has no available squad members; the coordinator will decline goals - Handle: {Handle}",
                handle);
            return;
        }

        todoProvider = new TodoProvider();
        backgroundAgents = new BackgroundAgentsProvider(delegates);

        var result = await CreateChatClientAgent(
            model,
            ThreadId,
            tools: null,
            configureOptions: agentOptions =>
            {
                agentOptions.AIContextProviders = [todoProvider, backgroundAgents];
                agentOptions.ChatOptions ??= new ChatOptions();
                agentOptions.ChatOptions.Instructions = BuildInstructions();
            });

        session = result.Session;
        harness = new LoopAgent(
            result.Agent,
            [new TodoCompletionLoopEvaluator(), new BackgroundTaskCompletionLoopEvaluator()],
            new LoopAgentOptions
            {
                MaxIterations = Math.Max(1, options.MaxLoopIterations),
                NonStreamingReturnsLastResponseOnly = true
            },
            loggerFactory);

        taskLogger.LogInformation(
            "Surface Task harness ready - Handle: {Handle}, Delegates: {DelegateCount} [{DelegateNames}], MaxIterations: {MaxIterations}, DelegationTimeoutSeconds: {DelegationTimeoutSeconds}",
            handle,
            delegates.Count,
            string.Join(", ", delegates.Select(d => d.Name)),
            options.MaxLoopIterations,
            options.DelegationTimeoutSeconds);
    }

    /// <summary>Test hook: the composed loop agent, or null when the squad has no usable members.</summary>
    internal AIAgent? HarnessAgent => harness;

    /// <summary>Test hook: the session the harness providers store their state in.</summary>
    internal AgentSession? HarnessSession => session;

    /// <summary>Test hook: the delegation agents projected from the squad roster.</summary>
    internal IReadOnlyList<SurfaceSquadMemberAgent> Delegates => delegates;

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            response.Message = "Send a goal for this task squad.";
            return response;
        }

        if (harness is null || session is null)
        {
            response.Message = excludedMembers.Count > 0
                ? $"No squad members are available. Unavailable: {string.Join("; ", excludedMembers)}"
                : "Add at least one executor agent before starting a Task squad goal.";
            return response;
        }

        taskLogger.LogInformation(
            "Surface Task harness starting run - Handle: {Handle}, From: {FromHandle}, GoalLength: {GoalLength}",
            fabrcoreAgentHost.GetHandle(),
            message.FromHandle,
            message.Message.Length);

        fabrcoreAgentHost.SetStatusMessage("Planning...");

        string text;
        try
        {
            var run = await harness.RunAsync(message.Message, session);
            text = run.Text;
        }
        catch (Exception ex)
        {
            taskLogger.LogError(
                ex,
                "Surface Task harness run failed - Handle: {Handle}",
                fabrcoreAgentHost.GetHandle());
            response.Message = $"The task squad could not finish this goal: {ex.Message}";
            await SendFinalAsync(response.Message);
            return response;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "The task squad finished but produced no summary.";
        }

        var remaining = await todoProvider!.GetRemainingTodosAsync(session);
        taskLogger.LogInformation(
            "Surface Task harness run complete - Handle: {Handle}, ResponseLength: {ResponseLength}, RemainingTodos: {RemainingTodos}",
            fabrcoreAgentHost.GetHandle(),
            text.Length,
            remaining.Count);

        if (remaining.Count > 0)
        {
            text += $"{Environment.NewLine}{Environment.NewLine}Note: {remaining.Count} item(s) were not completed within the iteration budget:{Environment.NewLine}"
                + string.Join(Environment.NewLine, remaining.Select(item => $"- {item.Title}"));
        }

        fabrcoreAgentHost.SetStatusMessage(string.Empty);
        await SendFinalAsync(text);

        response.Message = text;
        return response;
    }

    private string BuildInstructions()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You coordinate the \"{runtime.Squad.Name}\" task squad.");

        if (!string.IsNullOrWhiteSpace(runtime.Squad.Description))
        {
            sb.AppendLine(runtime.Squad.Description!.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("## How to work");
        sb.AppendLine("- Break the user's goal into todo items, then work through them.");
        sb.AppendLine("- You have no domain tools of your own. All execution happens by delegating to the squad members listed as background agents.");
        sb.AppendLine("- Delegate execution work only to members described as Executors. Start independent work concurrently rather than one at a time.");
        sb.AppendLine("- Members described as Subject matter experts are advisory. Consult them for guidance when a step is unclear or a delegation fails; do not assign them execution work.");
        sb.AppendLine("- If a delegation fails or comes back unusable, say so plainly. Consult an expert, retry with better instructions, or report the blocker. Never mark a todo complete on work that did not happen.");
        sb.AppendLine("- Finish with a consolidated answer to the original goal, not a list of what you delegated.");

        var persona = !string.IsNullOrWhiteSpace(runtime.Squad.TaskOptions.PersonaPrompt)
            ? runtime.Squad.TaskOptions.PersonaPrompt
            : config.SystemPrompt;

        if (!string.IsNullOrWhiteSpace(persona))
        {
            sb.AppendLine();
            sb.AppendLine("## Squad instructions");
            sb.AppendLine(persona!.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    private async Task SendFinalAsync(string text)
    {
        if (bus is null)
        {
            return;
        }

        await bus.MirrorAsync(new AgentMessage
        {
            FromHandle = fabrcoreAgentHost.GetHandle(),
            ToHandle = runtime.Squad.PrincipalHandle,
            MessageType = SurfaceSquadMessageTypes.Chat,
            Kind = MessageKind.Response,
            Message = text
        }, SurfaceSquadMessageTypes.Chat);
    }

    protected override Dictionary<string, string>? GetCustomHealthMetrics(HealthDetailLevel detailLevel)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TaskSquadName"] = runtime.Squad.Name,
            ["TaskSquadHandle"] = runtime.Squad.OrchestratorHandle,
            ["TaskSquadType"] = runtime.Squad.SquadType.ToString(),
            ["TaskAgentCount"] = runtime.Squad.Agents.Count.ToString(),
            ["TaskExecutorCount"] = CountRole(SurfaceSquadMemberRole.Executor).ToString(),
            ["TaskSmeCount"] = CountRole(SurfaceSquadMemberRole.SubjectMatterExpert).ToString(),
            ["TaskDelegateCount"] = delegates.Count.ToString(),
            ["TaskExcludedMembers"] = excludedMembers.Count.ToString(),
            ["TaskHarnessReady"] = (harness is not null).ToString(),
            ["TaskBusReady"] = (bus is not null).ToString(),
            ["TaskHasPersistedRuntimeArg"] = config.Args.ContainsKey(SurfaceSquadArgs.SquadDefinition).ToString()
        };

    private int CountRole(SurfaceSquadMemberRole role)
        => runtime.Squad.Agents.Count(agent => agent.Role == role);

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
}
