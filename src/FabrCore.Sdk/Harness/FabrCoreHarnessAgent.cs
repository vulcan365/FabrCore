#pragma warning disable MAAI001 // Harness providers (LoopAgent, BackgroundAgentsProvider, loop evaluators) are for evaluation purposes only and may change.
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// A FabrCore-assembled agent harness: todo tracking, operating modes, an iteration loop, and delegation to background
/// agents, composed over a caller-supplied <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is FabrCore's own assembler over the Microsoft Agent Framework's harness providers, not a wrapper
/// around <c>HarnessAgent</c>. Nothing is composed unless FabrCore composes it, so an upstream default-on
/// feature can never reach a tenant. Tool names stay Microsoft's (<c>todos_add</c>,
/// <c>background_agents_start_task</c>, …) so prompts remain portable.
/// </para>
/// <para>
/// Composition, outermost to innermost:
/// <list type="number">
/// <item><description><c>LoopAgent</c> — only when <see cref="FabrCoreHarnessOptions.LoopMode"/> or
/// <see cref="FabrCoreHarnessOptions.AdditionalLoopEvaluators"/> yields at least one evaluator.</description></item>
/// <item><description><c>OpenTelemetryAgent</c> — parity with <c>CreateChatClientAgent</c>, sensitive data on by default.</description></item>
/// <item><description><see cref="ChatClientAgent"/> with the todo, mode, skill, and background-agent context providers.</description></item>
/// </list>
/// The chat client keeps <c>ChatClientAgent</c>'s default middleware so FabrCore's
/// <c>TokenTrackingChatClient</c> stays the innermost client and run-safety sees every call.
/// </para>
/// <para>
/// Deliberately absent: file memory, file access, filesystem skill discovery, hosted web search, and tool
/// approval. The first four are rejected outright for a shared silo; tool approval is a later phase.
/// </para>
/// <para>
/// Compaction is composed by the caller, not here. <c>CreateFabrCoreHarnessAgent</c> passes a
/// <c>CompactionProvider</c> through <see cref="FabrCoreHarnessOptions.AIContextProviders"/> as layer 1 of
/// the ladder, and registers the history-compaction rungs separately — see <see cref="CompactionLadder"/>.
/// </para>
/// </remarks>
public sealed class FabrCoreHarnessAgent : DelegatingAIAgent
{
    /// <summary>
    /// The harness preamble prepended to an agent's own instructions when
    /// <see cref="FabrCoreHarnessOptions.HarnessInstructions"/> is not set.
    /// </summary>
    public const string DefaultInstructions =
        """
        You are a capable AI assistant working inside the FabrCore runtime. You use tools to complete tasks.

        ## How to work

        - Think the task through before acting. Break complex work into clear steps.
        - Track multi-step work with the todo tools: add the steps up front, then complete each one as you finish it. Never mark a todo complete for work that did not actually happen.
        - Say what you learned and what you are doing next between tool calls, so the person following along can see your reasoning.
        - Avoid making more than 4 tool calls in a row without explaining what you are doing.
        - If a tool call fails or returns something unexpected, adapt. Do not repeat the same call and expect a different result.
        - When background agents are available, delegate independent work to them and start that work concurrently rather than one item at a time. Read their replies critically — a reply is not proof the work was done correctly.
        - Finish with a clear, consolidated answer to what was actually asked, not a list of the steps you took.
        """;

    private readonly TodoProvider? todos;
    private readonly AgentModeProvider? modes;
    private readonly BackgroundAgentsProvider? backgroundAgents;
    private readonly AgentSkillsProvider? skills;
    private readonly int loopEvaluatorCount;

    /// <summary>
    /// Assembles a harness agent over <paramref name="chatClient"/>.
    /// </summary>
    /// <param name="chatClient">The chat client. Inside FabrCore this should come from <c>GetChatClient</c> so token tracking and run safety stay in the pipeline.</param>
    /// <param name="options">Composition options. <see langword="null"/> yields a todo-enabled, single-shot agent.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="services">Optional service provider used when building the agent pipeline.</param>
    /// <exception cref="ArgumentNullException"><paramref name="chatClient"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A requested loop mode is missing the provider or setting it depends on.</exception>
    public FabrCoreHarnessAgent(
        IChatClient chatClient,
        FabrCoreHarnessOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null)
        : this(Compose(chatClient, options, loggerFactory, services))
    {
    }

    private FabrCoreHarnessAgent(Composition composition)
        : base(composition.Agent)
    {
        todos = composition.Todos;
        modes = composition.Modes;
        backgroundAgents = composition.BackgroundAgents;
        skills = composition.Skills;
        PlanningModeName = composition.PlanningModeName;
        ExecutionModeName = composition.ExecutionModeName;
        MissingPlanModeBehavior = composition.MissingPlanModeBehavior;
        loopEvaluatorCount = composition.LoopEvaluatorCount;
    }

    /// <summary>The todo provider, or <see langword="null"/> when todos are disabled.</summary>
    public TodoProvider? Todos => todos;

    /// <summary>The operating-mode provider, or <see langword="null"/> when modes are disabled.</summary>
    public AgentModeProvider? Modes => modes;

    /// <summary>The mode selected by <see cref="HarnessMessageArgs.PlanMode"/> when its value is true.</summary>
    public string PlanningModeName { get; }

    /// <summary>The mode selected by <see cref="HarnessMessageArgs.PlanMode"/> when its value is false.</summary>
    public string ExecutionModeName { get; }

    /// <summary>Mode behavior for inbound messages that omit <see cref="HarnessMessageArgs.PlanMode"/>.</summary>
    public MissingPlanModeBehavior MissingPlanModeBehavior { get; }

    /// <summary>The background-agent provider, or <see langword="null"/> when no background agents were supplied.</summary>
    public BackgroundAgentsProvider? BackgroundAgents => backgroundAgents;

    /// <summary>The explicitly configured skill provider, or <see langword="null"/>.</summary>
    public AgentSkillsProvider? Skills => skills;

    /// <summary>True when a loop decorator is driving re-invocation; false for a single-shot agent.</summary>
    public bool IsLooping => loopEvaluatorCount > 0;

    private static Composition Compose(
        IChatClient chatClient,
        FabrCoreHarnessOptions? options,
        ILoggerFactory? loggerFactory,
        IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        var providers = new List<AIContextProvider>();

        TodoProvider? todoProvider = null;
        if (options?.DisableTodoProvider is not true)
        {
            todoProvider = new TodoProvider(options?.TodoProviderOptions);
            providers.Add(todoProvider);
        }

        var planningModeName = options?.PlanningModeName ?? "plan";
        var executionModeName = options?.ExecutionModeName ?? "execute";

        AgentModeProvider? modeProvider = null;
        if (options?.DisableAgentModeProvider is not true)
        {
            var modeOptions = BuildModeOptions(
                options?.AgentModeProviderOptions,
                planningModeName,
                executionModeName);
            modeProvider = new AgentModeProvider(modeOptions);
            providers.Add(modeProvider);
        }

        // Materialize once: the provider validates names on construction and the loop-mode check below
        // needs to know whether any delegates exist without re-enumerating a lazy sequence.
        var delegates = options?.BackgroundAgents?.ToList();

        BackgroundAgentsProvider? backgroundProvider = null;
        if (delegates is { Count: > 0 })
        {
            backgroundProvider = new BackgroundAgentsProvider(delegates, options?.BackgroundAgentsProviderOptions);
            providers.Add(backgroundProvider);
        }

        if (options?.AIContextProviders is { } extraProviders)
        {
            providers.AddRange(extraProviders);
        }

        AgentSkillsProvider? skillsProvider = null;
        if (options?.AgentSkillsSource is { } skillsSource)
        {
            var skillsOptions = options.AgentSkillsProviderOptions ?? new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
                DisableRunSkillScriptApproval = true,
                SkillsInstructionPrompt =
                    """
                    You have access to trusted, read-only skills containing domain-specific instructions and resources.
                    <available_skills>
                    {skills}
                    </available_skills>
                    When a task aligns with a skill, use `load_skill`, follow its instructions, and use
                    `read_skill_resource` for a listed resource when needed. Skill scripts are unavailable.
                    """
            };
            skillsProvider = new AgentSkillsProvider(skillsSource, skillsOptions, loggerFactory);
            providers.Add(skillsProvider);
        }

        var evaluators = BuildEvaluators(
            options,
            todoProvider is not null,
            modeProvider is not null,
            executionModeName,
            backgroundProvider is not null);

        var chatOptions = options?.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = CombineInstructions(
            options?.HarnessInstructions ?? DefaultInstructions,
            options?.ChatOptions?.Instructions);

        // ChatClientAgentOptions exposes no knob for FunctionInvokingChatClient.MaximumIterationsPerRequest.
        // Pre-wrapping is the least invasive route: WithDefaultAgentMiddleware skips inserting its own
        // function-invocation client when the supplied one already exposes it, so the remaining default
        // decorators (approval binding, approval bypass) keep their normal positions.
        var innerChatClient = chatClient;
        if (options?.MaximumIterationsPerRequest is int maxIterationsPerRequest)
        {
            innerChatClient = chatClient
                .AsBuilder()
                .UseFunctionInvocation(loggerFactory, ficc => ficc.MaximumIterationsPerRequest = maxIterationsPerRequest)
                .Build(services);
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Id = options?.Id,
            Name = options?.Name,
            Description = options?.Description,
            ChatOptions = chatOptions,
            ChatHistoryProvider = options?.ChatHistoryProvider,
            AIContextProviders = providers.Count > 0 ? providers : null
        };

        var builder = new ChatClientAgent(innerChatClient, agentOptions).AsBuilder();

        if (options?.DisableOpenTelemetry is not true)
        {
            var enableSensitiveData = options?.EnableSensitiveTelemetryData ?? true;
            builder.UseOpenTelemetry(
                options?.OpenTelemetrySourceName,
                cfg => cfg.EnableSensitiveData = enableSensitiveData);
        }

        AIAgent agent = builder.Build(services);

        if (evaluators.Count > 0)
        {
            var loopOptions = options?.LoopAgentOptions ?? new LoopAgentOptions
            {
                MaxIterations = options?.LoopMaxIterations is int max ? Math.Max(1, max) : null,
                NonStreamingReturnsLastResponseOnly = true
            };

            agent = new LoopAgent(agent, evaluators, loopOptions, loggerFactory);
        }

        return new Composition(
            agent,
            todoProvider,
            modeProvider,
            backgroundProvider,
            skillsProvider,
            planningModeName,
            executionModeName,
            options?.MissingPlanModeBehavior ?? MissingPlanModeBehavior.SelectPlanning,
            evaluators.Count);
    }

    private static List<LoopEvaluator> BuildEvaluators(
        FabrCoreHarnessOptions? options,
        bool hasTodoProvider,
        bool hasModeProvider,
        string executionModeName,
        bool hasBackgroundAgents)
    {
        var evaluators = new List<LoopEvaluator>();
        var mode = options?.LoopMode ?? HarnessLoopMode.None;

        if (mode.HasFlag(HarnessLoopMode.Todo))
        {
            if (!hasTodoProvider)
            {
                throw new ArgumentException(
                    "HarnessLoopMode.Todo requires the todo provider, but DisableTodoProvider is set.",
                    nameof(options));
            }

            evaluators.Add(hasModeProvider
                ? new TodoCompletionLoopEvaluator(new TodoCompletionLoopEvaluatorOptions
                {
                    Modes = [executionModeName]
                })
                : new TodoCompletionLoopEvaluator());
        }

        if (mode.HasFlag(HarnessLoopMode.Background))
        {
            if (!hasBackgroundAgents)
            {
                throw new ArgumentException(
                    "HarnessLoopMode.Background requires at least one background agent, but none were supplied.",
                    nameof(options));
            }

            evaluators.Add(new BackgroundTaskCompletionLoopEvaluator());
        }

        if (mode.HasFlag(HarnessLoopMode.Marker))
        {
            if (string.IsNullOrWhiteSpace(options?.LoopCompletionMarker))
            {
                throw new ArgumentException(
                    "HarnessLoopMode.Marker requires LoopCompletionMarker to be set.",
                    nameof(options));
            }

            evaluators.Add(new CompletionMarkerLoopEvaluator(options.LoopCompletionMarker));
        }

        if (mode.HasFlag(HarnessLoopMode.Judge))
        {
            if (options?.LoopJudgeChatClient is null)
            {
                throw new ArgumentException(
                    "HarnessLoopMode.Judge requires LoopJudgeChatClient to be set.",
                    nameof(options));
            }

            evaluators.Add(new AIJudgeLoopEvaluator(options.LoopJudgeChatClient, options.LoopJudgeOptions));
        }

        if (options?.AdditionalLoopEvaluators is { } additional)
        {
            evaluators.AddRange(additional);
        }

        return evaluators;
    }

    private static AgentModeProviderOptions BuildModeOptions(
        AgentModeProviderOptions? configured,
        string planningModeName,
        string executionModeName)
    {
        if (string.IsNullOrWhiteSpace(planningModeName))
        {
            throw new ArgumentException("PlanningModeName must not be null, empty, or whitespace.", nameof(FabrCoreHarnessOptions));
        }

        if (string.IsNullOrWhiteSpace(executionModeName))
        {
            throw new ArgumentException("ExecutionModeName must not be null, empty, or whitespace.", nameof(FabrCoreHarnessOptions));
        }

        if (string.Equals(planningModeName, executionModeName, StringComparison.Ordinal))
        {
            throw new ArgumentException("PlanningModeName and ExecutionModeName must be different.", nameof(FabrCoreHarnessOptions));
        }

        IReadOnlyList<AgentModeProviderOptions.AgentMode> effectiveModes = configured?.Modes ??
        [
            new AgentModeProviderOptions.AgentMode(planningModeName, DefaultPlanningModeInstructions),
            new AgentModeProviderOptions.AgentMode(executionModeName, DefaultExecutionModeInstructions)
        ];

        var names = effectiveModes.Select(mode => mode?.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(planningModeName))
        {
            throw new ArgumentException(
                $"PlanningModeName '{planningModeName}' is not present in AgentModeProviderOptions.Modes.",
                nameof(FabrCoreHarnessOptions));
        }

        if (!names.Contains(executionModeName))
        {
            throw new ArgumentException(
                $"ExecutionModeName '{executionModeName}' is not present in AgentModeProviderOptions.Modes.",
                nameof(FabrCoreHarnessOptions));
        }

        return new AgentModeProviderOptions
        {
            Instructions = configured?.Instructions,
            Modes = effectiveModes,
            DefaultMode = configured?.DefaultMode ?? planningModeName
        };
    }

    private const string DefaultPlanningModeInstructions =
        """
        Use this mode to understand the request and produce a plan for approval before execution.

        1. Analyze the request and use tools only for bounded exploration needed to remove uncertainty.
        2. Ask concise clarifying questions when the answer materially changes the plan.
        3. Create or update the durable todo list so it is the authoritative plan; do not write the plan to a file.
        4. Present the plan, important assumptions, and expected result to the user.
        5. Do not execute the planned work until the user approves it or explicitly asks you to switch to execute mode.
        """;

    private const string DefaultExecutionModeInstructions =
        """
        Use this mode to complete the user's request autonomously.

        1. Answer a genuinely simple question directly.
        2. For multi-step work, create any missing todos and treat the durable todo list as the work plan.
        3. Work through every todo using the available tools and best judgment; do not wait for feedback unless progress is genuinely blocked.
        4. Mark a todo complete only after its work has actually succeeded, and adapt when a tool fails or returns an unexpected result.
        5. Finish with a consolidated result and identify anything left incomplete when a runtime or iteration budget stops the run.
        """;

    private static string? CombineInstructions(string? harnessInstructions, string? agentInstructions)
    {
        var harness = string.IsNullOrWhiteSpace(harnessInstructions) ? null : harnessInstructions;
        var agent = string.IsNullOrWhiteSpace(agentInstructions) ? null : agentInstructions;

        return (harness, agent) switch
        {
            (null, null) => null,
            (null, not null) => agent,
            (not null, null) => harness,
            _ => $"{harness}\n\n{agent}"
        };
    }

    private sealed record Composition(
        AIAgent Agent,
        TodoProvider? Todos,
        AgentModeProvider? Modes,
        BackgroundAgentsProvider? BackgroundAgents,
        AgentSkillsProvider? Skills,
        string PlanningModeName,
        string ExecutionModeName,
        MissingPlanModeBehavior MissingPlanModeBehavior,
        int LoopEvaluatorCount);
}
