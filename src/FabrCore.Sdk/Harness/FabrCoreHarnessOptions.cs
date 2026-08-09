#pragma warning disable MAAI001 // Harness providers (LoopAgent, BackgroundAgentsProvider, loop evaluators) are for evaluation purposes only and may change.
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk;

/// <summary>
/// Configuration for a <see cref="FabrCoreHarnessAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the FabrCore counterpart to <c>HarnessAgentOptions</c>, deliberately narrower. It exposes
/// only the capabilities FabrCore composes: todos, operating modes, the iteration loop, and background delegation.
/// There is no file memory, no file access, no filesystem skill discovery, and no hosted web search —
/// those defaults are unsafe in a shared multi-tenant silo and are not offered even as options.
/// </para>
/// <para>
/// The whole compaction ladder is configured through <c>ModelConfiguration</c> and the
/// <c>_Context*</c> / <c>_Compaction*</c> / <c>_Projection*</c> args, not here. Layer 1 arrives as a
/// <c>CompactionProvider</c> in <see cref="AIContextProviders"/>; the history-compaction and run-safety
/// rungs are registered by the proxy against the chat history provider. See <see cref="CompactionLadder"/>.
/// </para>
/// </remarks>
public sealed class FabrCoreHarnessOptions
{
    /// <summary>Optional agent id.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// Agent name. Required to be non-empty when this agent is itself handed to another agent's
    /// background-agent provider, which rejects unnamed agents.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Optional agent description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Chat options carrying the agent's own tools and instructions.
    /// <see cref="Microsoft.Extensions.AI.ChatOptions.Instructions"/> is appended after
    /// <see cref="HarnessInstructions"/> to form the final instruction text.
    /// </summary>
    public ChatOptions? ChatOptions { get; set; }

    /// <summary>
    /// The harness preamble prepended to the agent's own instructions. <see langword="null"/> uses
    /// <see cref="FabrCoreHarnessAgent.DefaultInstructions"/>; an empty string drops the preamble entirely.
    /// </summary>
    public string? HarnessInstructions { get; set; }

    /// <summary>
    /// Chat history provider. Supply <see cref="FabrCoreChatHistoryProvider"/> so history persists to
    /// Orleans grain state and stays out of the session snapshot.
    /// </summary>
    public ChatHistoryProvider? ChatHistoryProvider { get; set; }

    /// <summary>Extra context providers appended after the harness's own.</summary>
    public IEnumerable<AIContextProvider>? AIContextProviders { get; set; }

    /// <summary>
    /// Explicit skill source. When set, the harness composes an <see cref="AgentSkillsProvider"/>.
    /// No current-directory or filesystem fallback is ever used.
    /// </summary>
    public AgentSkillsSource? AgentSkillsSource { get; set; }

    /// <summary>Optional configuration for the composed read-only skill provider.</summary>
    public AgentSkillsProviderOptions? AgentSkillsProviderOptions { get; set; }

    /// <summary>Set to <see langword="true"/> to omit the <c>mode_*</c> tools and operating-mode instructions.</summary>
    public bool DisableAgentModeProvider { get; set; }

    /// <summary>
    /// Optional mode configuration. When omitted, FabrCore supplies durable <c>plan</c> and <c>execute</c>
    /// modes whose planning instructions use todos rather than file memory.
    /// </summary>
    public AgentModeProviderOptions? AgentModeProviderOptions { get; set; }

    /// <summary>
    /// Mode selected when <see cref="HarnessMessageArgs.PlanMode"/> is <c>true</c>.
    /// Must name one of the effective <see cref="AgentModeProviderOptions.Modes"/>.
    /// </summary>
    public string PlanningModeName { get; set; } = "plan";

    /// <summary>
    /// Mode selected when <see cref="HarnessMessageArgs.PlanMode"/> is <c>false</c> and the only mode in
    /// which the built-in todo evaluator drives re-invocation.
    /// </summary>
    public string ExecutionModeName { get; set; } = "execute";

    /// <summary>
    /// Behavior when <see cref="FabrCoreHarnessResult.RunAsync(FabrCore.Core.AgentMessage, AgentRunOptions?, CancellationToken)"/>
    /// receives no <see cref="HarnessMessageArgs.PlanMode"/> argument. Defaults to the original planning behavior.
    /// </summary>
    public MissingPlanModeBehavior MissingPlanModeBehavior { get; set; } = MissingPlanModeBehavior.SelectPlanning;

    /// <summary>Set to <see langword="true"/> to omit the <c>todos_*</c> tools.</summary>
    public bool DisableTodoProvider { get; set; }

    /// <summary>Optional todo provider configuration (instructions, list-message rendering).</summary>
    public TodoProviderOptions? TodoProviderOptions { get; set; }

    /// <summary>
    /// Agents the model can delegate to through the <c>background_agents_*</c> tools. Each must have a
    /// non-empty, case-insensitively unique <see cref="AIAgent.Name"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="FabrCoreBackgroundAgent.FromRoster"/> produces a compliant set from FabrCore agent
    /// handles; callers with their own delegation semantics can supply any <see cref="AIAgent"/>.
    /// </remarks>
    public IEnumerable<AIAgent>? BackgroundAgents { get; set; }

    /// <summary>Optional background-agent provider configuration (instructions, roster rendering).</summary>
    public BackgroundAgentsProviderOptions? BackgroundAgentsProviderOptions { get; set; }

    /// <summary>Which evaluators drive the loop. <see cref="HarnessLoopMode.None"/> means single-shot.</summary>
    public HarnessLoopMode LoopMode { get; set; } = HarnessLoopMode.None;

    /// <summary>
    /// Iteration cap for the loop. <see langword="null"/> uses the framework default of 10.
    /// Ignored when <see cref="LoopAgentOptions"/> is supplied.
    /// </summary>
    public int? LoopMaxIterations { get; set; }

    /// <summary>
    /// Full loop configuration. When supplied it is used verbatim and
    /// <see cref="LoopMaxIterations"/> is ignored.
    /// </summary>
    public LoopAgentOptions? LoopAgentOptions { get; set; }

    /// <summary>Completion marker for <see cref="HarnessLoopMode.Marker"/>. Matched ordinally, case-sensitively.</summary>
    public string? LoopCompletionMarker { get; set; }

    /// <summary>
    /// Judge client for <see cref="HarnessLoopMode.Judge"/>. This is an <see cref="IChatClient"/>, not an
    /// agent — the judge runs bare, with no tools, session, or history.
    /// </summary>
    public IChatClient? LoopJudgeChatClient { get; set; }

    /// <summary>Optional judge instructions and criteria.</summary>
    public AIJudgeLoopEvaluatorOptions? LoopJudgeOptions { get; set; }

    /// <summary>Evaluators appended after the ones implied by <see cref="LoopMode"/>.</summary>
    public IEnumerable<LoopEvaluator>? AdditionalLoopEvaluators { get; set; }

    /// <summary>
    /// Cap on function-invocation iterations within a single model request.
    /// <see langword="null"/> leaves the framework default in place.
    /// </summary>
    public int? MaximumIterationsPerRequest { get; set; }

    /// <summary>Set to <see langword="true"/> to skip the OpenTelemetry agent decorator.</summary>
    public bool DisableOpenTelemetry { get; set; }

    /// <summary>
    /// Whether OpenTelemetry captures prompt and completion content.
    /// Defaults to <see langword="true"/> for parity with <c>CreateChatClientAgent</c>.
    /// </summary>
    public bool EnableSensitiveTelemetryData { get; set; } = true;

    /// <summary>Optional OpenTelemetry source name override.</summary>
    public string? OpenTelemetrySourceName { get; set; }
}
