using FabrCore.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace FabrCore.Sdk;

/// <summary>
/// Agent that creates and refines task plans from conversations using two-phase extraction.
/// Phase 1: Parallel context extraction (Summary, WorkItems+Blockers, PhaseStrategy).
/// Phase 2: Sequential refinement (Agent Assignment, Validation+Ordering).
/// Supports iterative replanning with diff tracking.
/// </summary>
public class TaskWorkingAgent : ITaskWorkingAgent
{
    private readonly IChatClient _chatClient;
    private readonly AgentSession _originalSession;
    private readonly ILogger? _logger;
    private readonly Func<string, string, Task>? _onProgress;
    private readonly ExecutionOptions? _executionOptions;
    private TaskTracking? _currentPlan;
    private readonly ExecutionState _executionState = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region Response Types for Structured Output

    private class SummaryResponse
    {
        public string Summary { get; set; } = string.Empty;
    }

    private class WorkItemsAndBlockersResponse
    {
        public List<WorkItem> WorkItems { get; set; } = [];
        public List<Blocker> Blockers { get; set; } = [];
    }

    private class PhaseStrategyResponse
    {
        public string Phase { get; set; } = "planning";
        public List<StrategyPivot> StrategyPivots { get; set; } = [];
    }

    private class AgentAssignmentsResponse
    {
        public List<AgentAssignment> AgentAssignments { get; set; } = [];
    }

    private class ValidationOrderingResponse
    {
        public List<WorkItem> ValidatedWorkItems { get; set; } = [];
        public List<string> ExecutionOrder { get; set; } = [];
        public List<string> CriticalPath { get; set; } = [];
        public string PlanRationale { get; set; } = string.Empty;
    }

    private class ReplanResponse
    {
        public string Summary { get; set; } = string.Empty;
        public List<WorkItem> WorkItems { get; set; } = [];
        public List<Blocker> Blockers { get; set; } = [];
        public string Phase { get; set; } = "planning";
        public List<StrategyPivot> StrategyPivots { get; set; } = [];
    }

    private class ResponseEvaluationResponse
    {
        public string Outcome { get; set; } = "completed";
        public string Summary { get; set; } = string.Empty;
        public string? FollowUpMessage { get; set; }
    }

    #endregion

    #region System Prompts

    private static readonly string SummarySystemPrompt = """
        You are a work status summarizer. Analyze the conversation and create a summary of the work being tracked.

        Your response must be a JSON object with a "summary" field containing 2-4 sentences that capture:
        - What is being worked on (the objective)
        - Current progress status
        - Why this work is being done (the rationale)
        """;

    private static readonly string WorkItemsAndBlockersSystemPrompt = """
        You are a task decomposition and blocker identification agent. Analyze the conversation and extract
        both work items AND blockers together, ensuring referential consistency between them.

        CRITICAL CONSTRAINT - CAPABILITY-DRIVEN TASK CREATION:
        You will be given a list of available agents with their specific capabilities.
        ONLY create work items that an available agent can actually perform using its listed capabilities.
        Do NOT create work items for:
        - Human-only tasks (e.g., "get stakeholder approval", "define strategy", "review results", "make decisions")
        - Tasks that no agent has a matching capability for
        - Aspirational or planning tasks that don't map to a concrete agent action
        - Process steps like "QA testing", "define scope", "design architecture"
        Every work item MUST map to a specific agent capability. If you cannot identify which agent
        capability would perform the task, do NOT create the work item.

        DECOMPOSITION PRINCIPLES:
        - Each task should be ATOMIC: one clear action, one clear outcome
        - Every task MUST have success criteria describing what "done" looks like
        - Use consistent IDs: wi-001, wi-002, wi-003, etc.
        - Dependencies ONLY between items in YOUR response: if B uses output of A, B MUST list A in dependencyIds
        - The owner field MUST be set to a specific agent ID whose capabilities match the task
        - Frame tasks in terms of what the agent can DO, not what a human would plan

        COMPLEXITY ESTIMATION:
        - "simple": Single step, straightforward action (e.g., send an email, look up a record)
        - "medium": 2-5 steps, some coordination needed (e.g., research + draft + send)
        - "complex": >5 steps, significant coordination or decision-making (e.g., full analysis pipeline)

        Your response must be a JSON object with:
        - "workItems": Array of work items
        - "blockers": Array of blockers

        Each work item should have:
        - id: Consistent ID format (wi-001, wi-002, etc.)
        - title: Short action-oriented name (e.g., "Search inactive customers", not "Define scope")
        - description: What the agent will do using its capability
        - status: "completed", "in_progress", "pending", "blocked", "cancelled", or "failed"
        - priority: "critical", "high", "medium", or "low" (null if not mentioned)
        - owner: The agent ID that will perform this task (MUST match an available agent whose capability fits)
        - result: The outcome (if completed, otherwise null)
        - blockedReason: Why it's blocked (if applicable, otherwise null)
        - parentId: Parent work item ID (if this is a subtask, otherwise null)
        - subTasks: Array of child work items (for task decomposition, or empty array)
        - dependencyIds: IDs of work items that must complete before this can start (ONLY IDs from this response)
        - successCriteria: What defines "done" for this task
        - attempts: How many times this task has been attempted (default 0)
        - estimatedComplexity: "simple", "medium", or "complex"

        Each blocker should have:
        - id: Unique identifier (b-001, b-002, etc.)
        - description: What is blocking progress
        - blocksWorkItemIds: Array of work item IDs this affects (MUST reference IDs from your workItems array)
        - severity: "critical", "major", or "minor"
        - resolution: Proposed fix or workaround (null if unknown)
        - status: "active" or "resolved"

        If no work items can be created from the conversation given the available agent capabilities, return empty arrays.
        """;

    private static readonly string PhaseAndStrategySystemPrompt = """
        You are a conversation phase analyzer. Analyze the conversation to identify the current phase and any strategy pivots.

        Your response must be a JSON object with:
        - phase: Current conversation phase - one of "planning", "execution", "recovery", or "complete"
        - strategyPivots: Array of strategy pivots detected

        Phase definitions:
        - "planning": High-level goals being broken into tasks, initial setup
        - "execution": Active implementation, running commands, making changes
        - "recovery": Retrying after failures, debugging, changing approach
        - "complete": All work finished, final summary/handoff

        Each strategy pivot should have:
        - id: Unique identifier (e.g., "pivot1", "p-001")
        - fromApproach: The previous approach or strategy
        - toApproach: The new approach being taken
        - reason: Why the pivot occurred
        - affectedWorkItemIds: IDs of work items affected (or empty array)

        If no pivots are detected, return an empty array for strategyPivots.
        """;

    private static readonly string AgentAssignmentSystemPrompt = """
        You are an agent assignment planner. You receive the ACTUAL extracted work items as structured data,
        plus a list of available agents with their capabilities.

        Your job is to validate and create agent-to-work-item assignments based STRICTLY on capabilities.

        Your response must be a JSON object with an "agentAssignments" array. Each assignment should have:
        - agentId: The ID of the agent being assigned (MUST match an available agent ID exactly)
        - workItemId: The ID of the work item (MUST match a work item ID from the provided list exactly)
        - rationale: Brief explanation citing the SPECIFIC capability that matches the task
        - capability: The specific capability being used (MUST match one of the agent's listed capabilities EXACTLY)

        STRICT ASSIGNMENT RULES:
        - The capability field MUST be an exact match to one of the agent's listed capabilities
        - If a work item's owner field references an agent, VERIFY that agent actually has a capability that fits
        - If the owner's capabilities do NOT fit, reassign to the correct agent or leave unassigned
        - Do NOT force-fit: if no agent has a capability that genuinely matches the task, do NOT assign it
        - Do NOT assign agents to planning, decision-making, approval, or review tasks — agents perform actions, not judgments
        - Only assign to pending and in_progress work items
        - An agent can be assigned to multiple tasks if their capabilities apply
        """;

    private static readonly string ValidationOrderingSystemPrompt = """
        You are a plan validator and execution order planner. You receive a fully assembled task plan
        and must validate it, fix any issues, and determine execution ordering.

        Your response must be a JSON object with:
        - validatedWorkItems: The work items array with any fixes applied (remove invalid items, fix refs, etc.)
        - executionOrder: Array of work item IDs in execution order (topological sort respecting dependencies + priorities)
        - criticalPath: Array of work item IDs forming the longest dependency chain (the bottleneck)
        - planRationale: 2-4 sentences explaining the overall strategy and key decisions

        VALIDATION RULES:
        - REMOVE work items that have no agent assignment (no owner or no matching agentAssignment) — these cannot be executed
        - REMOVE work items that describe human-only tasks (approvals, decisions, strategy, reviews) — agents perform actions
        - Remove any dependencyIds that reference non-existent work items
        - If a work item has status "blocked" but no blockedReason, add a reason
        - Ensure all subtask parentIds reference existing work items
        - Fix any blocker blocksWorkItemIds that reference non-existent work items

        EXECUTION ORDER RULES:
        - Dependencies must be satisfied: if B depends on A, A must come before B
        - Higher priority items come first when no dependency constraints apply
        - Already completed items come first (they're done)
        - Blocked items come last

        CRITICAL PATH:
        - The longest chain of dependent work items from start to finish
        - This is the bottleneck that determines minimum completion time
        """;

    private static readonly string ReplanSystemPrompt = """
        You are a plan revision agent. You receive a previous task plan (as structured JSON) along with
        status updates and new context. Your job is to produce an updated plan.

        CRITICAL STATUS RULES:
        - Do NOT change any work item's status based on conversation content alone
        - Status changes ONLY come from the STATUS UPDATES section — if a work item is not listed
          in STATUS UPDATES, its status MUST remain exactly as it was in the previous plan
        - The conversation may contain information that SEEMS like a task was completed, but unless
          the STATUS UPDATES explicitly confirm it, the task status stays unchanged
        - Status updates have already been applied to the plan you receive — do not re-apply them

        FAILURE HANDLING:
        - When a task has status "failed", analyze the failure reason in the result field
        - If the failure is permanent (permission denied, capability not supported, invalid request):
          mark it as "cancelled" and replan dependent tasks around it — reassign to a different agent,
          create alternative tasks, or remove tasks that can no longer proceed
        - If dependent tasks can still be accomplished via an alternative approach, create new tasks
          to replace the failed path
        - Record a strategy pivot explaining why the approach changed
        - Update blockers if the failure creates new blockers for other tasks

        CROSS-TASK INTELLIGENCE:
        - When a completed task's result contains information that satisfies another pending task's
          success criteria, mark that pending task as "completed" with a result noting it was
          fulfilled by the other task's output (e.g., "Fulfilled by wi-003: [relevant data]")
        - When a completed task's result reveals that a pending task is no longer needed
          (redundant, already done, or irrelevant given new information), mark it as "cancelled"
        - Always review completed task results to see if they provide data, answers, or outputs
          that other tasks were going to separately fetch or produce

        PLAN STRUCTURE RULES:
        - ADD new tasks ONLY if they can be performed by an available agent's capabilities
        - REMOVE cancelled tasks (don't include them in the output)
        - ADJUST dependencies based on what has changed
        - Use the same ID format (wi-001, wi-002, etc.) and continue numbering from where the previous plan left off
        - Every work item MUST have an owner agent with a capability that matches the task
        - Do NOT add human-only tasks (approvals, decisions, reviews, strategy) that no agent can perform
        - KEEP completed tasks exactly as they are — never modify completed items (unless marking
          as fulfilled by cross-task intelligence as described above)

        WHAT THE CONVERSATION CAN INFORM:
        - New requirements or tasks the user wants added
        - Clarifications about existing task scope or priorities
        - Changes to dependencies or ordering
        - New blockers discovered in discussion

        WHAT THE CONVERSATION CANNOT INFORM:
        - Task completion (only agents report completion via STATUS UPDATES)
        - Task failure (only agents report failure via STATUS UPDATES)
        - Task progress (only agents report progress via STATUS UPDATES)

        Your response must be a JSON object with:
        - summary: Updated 2-4 sentence summary of the work status
        - workItems: Complete updated array of all work items (including completed ones unchanged)
        - blockers: Updated array of blockers
        - phase: Updated conversation phase
        - strategyPivots: Any new strategy pivots (include previous ones plus any new ones)
        """;

    private static readonly string ResponseEvaluationSystemPrompt = """
        You evaluate whether a worker agent's response truly completes the assigned work item
        with enough substance that downstream work items can use the output.

        Given:
        - The work item title, description, and success criteria
        - The agent's response
        - Context from other completed work items (if any)
        - Downstream dependent work items that will consume this work item's result (if any)

        Determine the outcome:
        - "completed": The response contains the ACTUAL deliverable data or output described in the
          success criteria. Downstream work items will have the concrete information they need.
        - "needsInfo": The agent needs more information OR the response is missing the actual
          deliverable. This includes responses that claim completion but do not contain the data,
          records, content, or output that the success criteria or downstream tasks require.
        - "failed": The agent explicitly cannot perform the task or encountered an unrecoverable error.

        CRITICAL — DATA COMPLETENESS RULES:
        - A response that CLAIMS to have done work ("I found 28 customers", "I have compiled the list",
          "The report is ready") but does NOT include the actual data in the response is "needsInfo".
          Claiming results exist is NOT the same as delivering them.
        - If the success criteria says to produce a list, extract records, gather data, or generate
          content — the actual items, records, data, or content MUST be present in the response text.
          A summary count alone ("found 28 results") is NOT sufficient.
        - If downstream work items need specific data from this result (e.g., customer emails, names,
          IDs, amounts), that data must be explicitly present in the response, not just referenced.
        - Simulated or placeholder data IS acceptable (this may be a demo/test agent) — the key is
          that the response contains substantive output, not just a meta-description of output.

        QUESTION DETECTION RULES:
        - If the response contains questions directed at the requester ("Would you like...",
          "Could you provide...", "What format..."), that is "needsInfo"
        - Rhetorical questions or offers of additional help at the END of an otherwise complete
          response (e.g., "Here is the list: [...]. Would you like anything else?") are acceptable
          and should still be "completed" IF the actual deliverable is present.

        FOLLOW-UP MESSAGE RULES:
        When outcome is "needsInfo", you MUST provide a followUpMessage that:
        1. Explicitly tells the agent to include the actual data/records/content in its response —
           not just a summary or count
        2. Answers any questions the agent asked, using context from completed work items if possible
        3. Describes what downstream work items need from this output so the agent understands
           why the full data is required
        4. If the agent asked about format, instruct it to provide the data in plain text,
           listing all records with the relevant fields

        Your response must be a JSON object with:
        - outcome: "completed", "needsInfo", or "failed"
        - summary: Brief explanation of why you chose this outcome (1-2 sentences)
        - followUpMessage: The message to send back to the agent (required when outcome is "needsInfo", null otherwise)
        """;

    private static readonly string DispatchMessageSystemPrompt = """
        You generate dispatch messages for worker agents. Your job is to produce a clear, actionable
        message that gives the worker agent everything it needs to complete its assigned task with
        accuracy and integrity.

        You will receive:
        - The work item (title, description, success criteria)
        - Results from completed work items, especially those this task depends on
        - The overall plan summary for context

        YOUR MESSAGE MUST:
        - State clearly what the agent needs to do
        - Include ALL relevant data and results from completed work items that the agent needs
          (e.g., customer lists, names, IDs, amounts, dates — pass the actual data, not references)
        - Provide specific details so the agent can perform the work without asking follow-up questions
        - Be self-contained: the agent should NOT need to look up information from other sources
          that is already available from completed work

        YOUR MESSAGE MUST NOT:
        - Include meta-commentary about the plan or execution process
        - Reference work item IDs or plan structure
        - Ask the agent questions — give it clear instructions

        Write the message as a direct instruction to the agent. Be concise but complete.
        """;

    #endregion

    #region Effort Level Instructions

    private static string GetEffortLevelInstructions(EffortLevel effortLevel) => effortLevel switch
    {
        EffortLevel.Quick =>
            "\n\nEFFORT LEVEL: QUICK — Create ONLY the minimum work items needed to accomplish the core task. " +
            "Do NOT include monitoring, follow-up, verification, or notification tasks. " +
            "Combine related actions into a single work item rather than splitting them.",
        EffortLevel.Standard =>
            "\n\nEFFORT LEVEL: STANDARD — Create work items for the core task plus reasonable verification and follow-up. " +
            "Include a verification or confirmation step if the task outcome can be checked. " +
            "Include one follow-up step if the task involves communication.",
        EffortLevel.Thorough =>
            "\n\nEFFORT LEVEL: THOROUGH — Create a comprehensive plan with full coverage. " +
            "Include monitoring tasks, follow-up tasks (e.g., reminders if no response), " +
            "contingency tasks (e.g., escalation if blocked), and notification tasks (e.g., inform requestor of outcome). " +
            "Think about what could go wrong and add mitigation steps.",
        _ => ""
    };

    private static string GetEffortLevelReplanInstructions(EffortLevel effortLevel) => effortLevel switch
    {
        EffortLevel.Quick => "\n\nEFFORT LEVEL: QUICK — Keep plan minimal when adding new tasks.",
        EffortLevel.Standard => "\n\nEFFORT LEVEL: STANDARD — Maintain balanced approach when adding new tasks.",
        EffortLevel.Thorough =>
            "\n\nEFFORT LEVEL: THOROUGH — Maintain comprehensive coverage, add contingency for new risks.",
        _ => ""
    };

    #endregion

    /// <summary>
    /// Creates a TaskWorkingAgent that references the original session.
    /// </summary>
    /// <param name="chatClient">The chat client to use for extraction.</param>
    /// <param name="originalSession">The session to fork and analyze.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="onProgress">Optional async callback for progress reporting: (phase, message) => Task.</param>
    /// <param name="executionOptions">Optional execution options for running the execution loop.</param>
    public TaskWorkingAgent(
        IChatClient chatClient,
        AgentSession originalSession,
        ILogger? logger = null,
        Func<string, string, Task>? onProgress = null,
        ExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(originalSession);

        _chatClient = chatClient;
        _originalSession = originalSession;
        _logger = logger;
        _onProgress = onProgress;
        _executionOptions = executionOptions;

        _logger?.LogDebug("Created TaskWorkingAgent for session");
    }

    private async Task ReportProgressAsync(string phase, string message)
    {
        _logger?.LogInformation("TaskWorkingAgent: {Phase} - {Message}", phase, message);
        if (_onProgress != null)
            await _onProgress(phase, message);
    }

    /// <summary>
    /// Creates an initial task plan from the conversation using two-phase extraction.
    /// Phase 1: Parallel extraction of Summary, WorkItems+Blockers, PhaseStrategy.
    /// Phase 2: Sequential Agent Assignment and Validation+Ordering using Phase 1 output.
    /// </summary>
    public async Task<TaskTracking> PlanAsync(
        IReadOnlyList<AvailableAgent>? availableAgents = null,
        string? extractionPrompt = null,
        EffortLevel effortLevel = EffortLevel.Quick,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("Starting two-phase task planning (effort: {EffortLevel})", effortLevel);
        await ReportProgressAsync("planning", "Analyzing conversation...");

        using var session = await CreatePlanningSessionAsync(cancellationToken);
        var extraContext = string.IsNullOrEmpty(extractionPrompt) ? "" : $" {extractionPrompt}";
        var agentsContext = BuildAgentsContext(availableAgents);

        // Phase 1: Parallel context extraction
        await ReportProgressAsync("planning", "Extracting work items and blockers...");
        var (summaryResult, workItemsBlockersResult, phaseStrategyResult) =
            await RunPhase1ExtractionsAsync(session, extraContext, agentsContext, effortLevel);

        var workItems = workItemsBlockersResult?.WorkItems ?? [];
        var blockers = workItemsBlockersResult?.Blockers ?? [];

        await ReportProgressAsync("planning", $"Found {workItems.Count} work items.");

        // Phase 2: Agent assignment + validation
        var (agentAssignments, validationResult) =
            await RefineAndValidateAsync(workItems, blockers, availableAgents, session, "planning");

        // Assemble final plan
        var tracking = new TaskTracking
        {
            Summary = summaryResult?.Summary ?? string.Empty,
            AllWork = validationResult?.ValidatedWorkItems ?? workItems,
            Blockers = blockers,
            AgentAssignments = agentAssignments,
            Phase = phaseStrategyResult?.Phase ?? "planning",
            StrategyPivots = phaseStrategyResult?.StrategyPivots ?? [],
            ExecutionOrder = validationResult?.ExecutionOrder ?? [],
            CriticalPath = validationResult?.CriticalPath ?? [],
            PlanRationale = validationResult?.PlanRationale,
            EffortLevel = effortLevel,
            PlanVersion = 1,
            PlannedAt = DateTime.UtcNow
        };

        PlanValidator.Validate(tracking);

        _logger?.LogInformation(
            "TaskWorkingAgent: Planning complete in {Elapsed:F1}s - {WorkItemCount} work items, {BlockerCount} blockers, {AssignmentCount} assignments, phase: {Phase}, {ExecutionOrderCount} execution steps, {CriticalPathCount} critical path items",
            session.Elapsed.TotalSeconds, tracking.AllWork.Count, tracking.Blockers.Count,
            tracking.AgentAssignments.Count, tracking.Phase,
            tracking.ExecutionOrder.Count, tracking.CriticalPath.Count);

        await ReportProgressAsync("planning", $"Plan complete — {tracking.AllWork.Count} tasks, {tracking.AgentAssignments.Count} assignments");

        _currentPlan = tracking;
        return tracking;
    }

    /// <summary>
    /// Updates an existing plan based on new information and status changes.
    /// Applies status updates in code first, then uses LLM for replanning, then validates.
    /// </summary>
    public async Task<TaskTracking> ReplanAsync(
        ReplanContext replanContext,
        IReadOnlyList<AvailableAgent>? availableAgents = null,
        string? extractionPrompt = null,
        EffortLevel? effortLevel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replanContext);
        ArgumentNullException.ThrowIfNull(replanContext.PreviousPlan);

        var resolvedEffortLevel = effortLevel ?? replanContext.PreviousPlan.EffortLevel;
        _logger?.LogDebug("Starting replan from version {Version} (effort: {EffortLevel})", replanContext.PreviousPlan.PlanVersion, resolvedEffortLevel);

        await ReportProgressAsync("replanning", "Applying status updates...");

        using var session = await CreatePlanningSessionAsync(cancellationToken);
        var updatedPlan = ApplyStatusUpdates(replanContext.PreviousPlan, replanContext.StatusUpdates);
        var extraContext = string.IsNullOrEmpty(extractionPrompt) ? "" : $" {extractionPrompt}";
        var agentsContext = BuildAgentsContext(availableAgents);

        // Replan extraction (single LLM call)
        await ReportProgressAsync("replanning", "Updating plan...");
        var replanUserPrompt = BuildReplanUserPrompt(updatedPlan, replanContext, agentsContext, extraContext);
        var replanPromptWithEffort = ReplanSystemPrompt + GetEffortLevelReplanInstructions(resolvedEffortLevel);
        var replanSchema = AIJsonUtilities.CreateJsonSchema(typeof(ReplanResponse));
        var replanResult = await ExtractAsync<ReplanResponse>(
            "Replan", replanPromptWithEffort, replanSchema, "ReplanResponse",
            replanUserPrompt, session.Forked, session.RunningExtractions, session.Token);

        // Agent re-assignment + validation
        var workItems = replanResult?.WorkItems ?? updatedPlan.AllWork;
        var blockers = replanResult?.Blockers ?? updatedPlan.Blockers;
        var (agentAssignments, validationResult) =
            await RefineAndValidateAsync(workItems, blockers, availableAgents, session, "replanning");

        var diff = ComputeDiff(replanContext.PreviousPlan, workItems, agentAssignments);

        // Assemble updated plan
        var tracking = new TaskTracking
        {
            Summary = replanResult?.Summary ?? updatedPlan.Summary,
            AllWork = validationResult?.ValidatedWorkItems ?? workItems,
            Blockers = blockers,
            AgentAssignments = agentAssignments,
            Phase = replanResult?.Phase ?? updatedPlan.Phase,
            StrategyPivots = replanResult?.StrategyPivots ?? updatedPlan.StrategyPivots,
            ExecutionOrder = validationResult?.ExecutionOrder ?? [],
            CriticalPath = validationResult?.CriticalPath ?? [],
            PlanRationale = validationResult?.PlanRationale ?? updatedPlan.PlanRationale,
            EffortLevel = resolvedEffortLevel,
            PlanVersion = replanContext.PreviousPlan.PlanVersion + 1,
            PlannedAt = DateTime.UtcNow,
            LastReplanDiff = diff
        };

        PlanValidator.Validate(tracking);

        _logger?.LogInformation(
            "TaskWorkingAgent: Replan v{Version} complete in {Elapsed:F1}s - {Added} added, {Removed} removed, {StatusChanged} status changed",
            tracking.PlanVersion, session.Elapsed.TotalSeconds,
            diff.AddedWorkItemIds.Count, diff.RemovedWorkItemIds.Count, diff.StatusChangedWorkItemIds.Count);

        await ReportProgressAsync("replanning", $"Replan complete — v{tracking.PlanVersion}, {diff.ChangeSummary}");

        _currentPlan = tracking;
        return tracking;
    }

    #region Planning Infrastructure

    private sealed class PlanningSession(
        ForkedSessionResult forked,
        ConcurrentDictionary<string, DateTime> runningExtractions,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken,
        DateTime startTime) : IDisposable
    {
        public ForkedSessionResult Forked => forked;
        public ConcurrentDictionary<string, DateTime> RunningExtractions => runningExtractions;
        public CancellationTokenSource TimeoutCts => timeoutCts;
        public CancellationToken ExternalToken => externalToken;
        public DateTime StartTime => startTime;
        public CancellationToken Token => timeoutCts.Token;
        public TimeSpan Elapsed => DateTime.UtcNow - startTime;
        public void Dispose() => timeoutCts.Dispose();
    }

    private async Task<PlanningSession> CreatePlanningSessionAsync(CancellationToken cancellationToken)
    {
        var forked = await _originalSession.ForkAsync(_chatClient, logger: _logger, cancellationToken: cancellationToken);
        _logger?.LogDebug("Forked session with {MessageCount} messages", forked.HistoryProvider.OriginalMessageCount);

        var runningExtractions = new ConcurrentDictionary<string, DateTime>();
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        return new PlanningSession(forked, runningExtractions, timeoutCts, cancellationToken, DateTime.UtcNow);
    }

    private async Task<(SummaryResponse?, WorkItemsAndBlockersResponse?, PhaseStrategyResponse?)>
        RunPhase1ExtractionsAsync(
            PlanningSession session,
            string extraContext,
            string agentsContext,
            EffortLevel effortLevel)
    {
        var workItemsBlockersPrompt = WorkItemsAndBlockersSystemPrompt + GetEffortLevelInstructions(effortLevel);

        var summarySchema = AIJsonUtilities.CreateJsonSchema(typeof(SummaryResponse));
        var workItemsBlockersSchema = AIJsonUtilities.CreateJsonSchema(typeof(WorkItemsAndBlockersResponse));
        var phaseStrategySchema = AIJsonUtilities.CreateJsonSchema(typeof(PhaseStrategyResponse));

        var summaryTask = ExtractAsync<SummaryResponse>(
            "Summary", SummarySystemPrompt, summarySchema, "SummaryResponse",
            $"Analyze the conversation and summarize the overall work status.{extraContext}",
            session.Forked, session.RunningExtractions, session.Token);

        var workItemsBlockersTask = ExtractAsync<WorkItemsAndBlockersResponse>(
            "WorkItems+Blockers", workItemsBlockersPrompt, workItemsBlockersSchema, "WorkItemsAndBlockersResponse",
            $"Identify work items and blockers. Assign each task to an appropriate agent based on capabilities.{agentsContext}{extraContext}",
            session.Forked, session.RunningExtractions, session.Token);

        var phaseStrategyTask = ExtractAsync<PhaseStrategyResponse>(
            "PhaseStrategy", PhaseAndStrategySystemPrompt, phaseStrategySchema, "PhaseStrategyResponse",
            $"Determine the current conversation phase and identify any strategy pivots.{extraContext}",
            session.Forked, session.RunningExtractions, session.Token);

        var progressTask = LogProgressAsync(session.RunningExtractions, session.StartTime, session.Token);

        try
        {
            await Task.WhenAll(summaryTask, workItemsBlockersTask, phaseStrategyTask);
        }
        catch (OperationCanceledException) when (session.TimeoutCts.IsCancellationRequested && !session.ExternalToken.IsCancellationRequested)
        {
            _logger?.LogWarning("TaskWorkingAgent: Phase 1 timed out after 5 minutes");
        }

        var summaryResult = await summaryTask;
        var workItemsBlockersResult = await workItemsBlockersTask;
        var phaseStrategyResult = await phaseStrategyTask;

        var workItemCount = workItemsBlockersResult?.WorkItems?.Count ?? 0;
        var blockerCount = workItemsBlockersResult?.Blockers?.Count ?? 0;
        _logger?.LogInformation("TaskWorkingAgent: Phase 1 complete in {Elapsed:F1}s - {WorkItemCount} work items, {BlockerCount} blockers",
            session.Elapsed.TotalSeconds, workItemCount, blockerCount);

        return (summaryResult, workItemsBlockersResult, phaseStrategyResult);
    }

    private async Task<(List<AgentAssignment> assignments, ValidationOrderingResponse? validation)>
        RefineAndValidateAsync(
            List<WorkItem> workItems,
            List<Blocker> blockers,
            IReadOnlyList<AvailableAgent>? availableAgents,
            PlanningSession session,
            string progressPhase)
    {
        await ReportProgressAsync(progressPhase, "Assigning agents to tasks...");
        List<AgentAssignment> agentAssignments = [];
        if (availableAgents is { Count: > 0 } && workItems.Count > 0)
        {
            agentAssignments = await RunAgentAssignmentAsync(workItems, availableAgents, session);
        }

        await ReportProgressAsync(progressPhase, "Validating plan and ordering tasks...");
        var validationResult = await RunValidationOrderingAsync(workItems, blockers, agentAssignments, session);

        try { await session.TimeoutCts.CancelAsync(); } catch { }

        return (agentAssignments, validationResult);
    }

    #endregion

    #region Phase 2 Helpers

    private async Task<List<AgentAssignment>> RunAgentAssignmentAsync(
        List<WorkItem> workItems,
        IReadOnlyList<AvailableAgent> availableAgents,
        PlanningSession session)
    {
        var workItemsJson = JsonSerializer.Serialize(workItems.Select(w => new
        {
            w.Id, w.Title, w.Status, w.Owner, w.EstimatedComplexity,
            dependencyIds = w.DependencyIds
        }), JsonOptions);

        var agentsContext = BuildAgentsContext(availableAgents);

        var schema = AIJsonUtilities.CreateJsonSchema(typeof(AgentAssignmentsResponse));
        var result = await ExtractAsync<AgentAssignmentsResponse>(
            "AgentAssignment", AgentAssignmentSystemPrompt, schema, "AgentAssignmentsResponse",
            $"WORK ITEMS TO ASSIGN:\n{workItemsJson}{agentsContext}\n\nCreate optimal agent assignments for the pending and in-progress work items.",
            session.Forked, session.RunningExtractions, session.Token);

        return result?.AgentAssignments ?? [];
    }

    private async Task<ValidationOrderingResponse?> RunValidationOrderingAsync(
        List<WorkItem> workItems,
        List<Blocker> blockers,
        List<AgentAssignment> agentAssignments,
        PlanningSession session)
    {
        var planJson = JsonSerializer.Serialize(new
        {
            workItems = workItems,
            blockers = blockers,
            agentAssignments = agentAssignments.Select(a => new { a.AgentId, a.WorkItemId })
        }, JsonOptions);

        var schema = AIJsonUtilities.CreateJsonSchema(typeof(ValidationOrderingResponse));
        return await ExtractAsync<ValidationOrderingResponse>(
            "ValidationOrdering", ValidationOrderingSystemPrompt, schema, "ValidationOrderingResponse",
            $"PLAN TO VALIDATE AND ORDER:\n{planJson}\n\nValidate the plan, fix any issues, and determine execution order and critical path.",
            session.Forked, session.RunningExtractions, session.Token);
    }

    #endregion

    #region Replan Helpers

    private static string BuildReplanUserPrompt(
        TaskTracking updatedPlan,
        ReplanContext replanContext,
        string agentsContext,
        string extraContext)
    {
        var previousPlanJson = JsonSerializer.Serialize(new
        {
            summary = updatedPlan.Summary,
            workItems = updatedPlan.AllWork,
            blockers = updatedPlan.Blockers,
            phase = updatedPlan.Phase,
            strategyPivots = updatedPlan.StrategyPivots
        }, JsonOptions);

        var statusUpdateSection = replanContext.StatusUpdates.Count > 0
            ? $"\n\nSTATUS UPDATES ALREADY APPLIED:\n{string.Join("\n", replanContext.StatusUpdates.Select(u => $"- {u.WorkItemId}: {u.NewStatus}{(u.Result != null ? $" ({u.Result})" : "")}"))}"
            : "";

        var newContextSection = replanContext.NewContext.Count > 0
            ? $"\n\nNEW CONTEXT:\n{string.Join("\n", replanContext.NewContext.Select((c, i) => $"- {c}"))}"
            : "";

        return $"PREVIOUS PLAN:\n{previousPlanJson}{statusUpdateSection}{newContextSection}{agentsContext}{extraContext}\n\nReview the conversation for any NEW tasks or requirements the user has mentioned. Do NOT infer status changes from the conversation — only the STATUS UPDATES section reflects actual agent execution results. Produce an updated plan.";
    }

    /// <summary>
    /// Applies status updates to a plan in pure code (no LLM needed).
    /// </summary>
    internal static TaskTracking ApplyStatusUpdates(TaskTracking plan, List<TaskStatusUpdate> updates)
    {
        if (updates.Count == 0)
            return plan;

        // Deep copy the work items
        var updatedWork = plan.AllWork.Select(w => new WorkItem
        {
            Id = w.Id,
            Title = w.Title,
            Description = w.Description,
            Status = w.Status,
            Priority = w.Priority,
            Owner = w.Owner,
            Result = w.Result,
            BlockedReason = w.BlockedReason,
            ParentId = w.ParentId,
            SubTasks = w.SubTasks,
            DependencyIds = new List<string>(w.DependencyIds),
            SuccessCriteria = w.SuccessCriteria,
            Attempts = w.Attempts,
            EstimatedComplexity = w.EstimatedComplexity,
            ExecutionOrder = w.ExecutionOrder
        }).ToList();

        foreach (var update in updates)
        {
            var workItem = updatedWork.FirstOrDefault(w =>
                string.Equals(w.Id, update.WorkItemId, StringComparison.OrdinalIgnoreCase));
            if (workItem != null)
            {
                workItem.Status = update.NewStatus;
                if (update.Result != null)
                    workItem.Result = update.Result;
            }
        }

        return new TaskTracking
        {
            Summary = plan.Summary,
            AllWork = updatedWork,
            Blockers = plan.Blockers,
            AgentAssignments = plan.AgentAssignments,
            Phase = plan.Phase,
            StrategyPivots = plan.StrategyPivots,
            ExecutionOrder = plan.ExecutionOrder,
            CriticalPath = plan.CriticalPath,
            PlanRationale = plan.PlanRationale,
            EffortLevel = plan.EffortLevel,
            PlanVersion = plan.PlanVersion,
            PlannedAt = plan.PlannedAt
        };
    }

    /// <summary>
    /// Computes the diff between the old plan and new work items/assignments.
    /// </summary>
    internal static PlanDiff ComputeDiff(
        TaskTracking oldPlan,
        List<WorkItem> newWorkItems,
        List<AgentAssignment> newAssignments)
    {
        var oldIds = new HashSet<string>(oldPlan.AllWork.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
        var newIds = new HashSet<string>(newWorkItems.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);

        // Build maps safely — LLM may return duplicate IDs (last wins)
        var oldWorkMap = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in oldPlan.AllWork) oldWorkMap[w.Id] = w;

        var added = newIds.Except(oldIds, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = oldIds.Except(newIds, StringComparer.OrdinalIgnoreCase).ToList();

        var statusChanged = new List<string>();
        var depsChanged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in newWorkItems)
        {
            // Skip duplicate IDs (already compared the first occurrence)
            if (!seen.Add(item.Id)) continue;

            if (oldWorkMap.TryGetValue(item.Id, out var oldItem))
            {
                if (!string.Equals(item.Status, oldItem.Status, StringComparison.OrdinalIgnoreCase))
                    statusChanged.Add(item.Id);
                if (!item.DependencyIds.SequenceEqual(oldItem.DependencyIds))
                    depsChanged.Add(item.Id);
            }
        }

        // Check reassignments — build map safely (last wins for duplicate work item assignments)
        var oldAssignmentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in oldPlan.AgentAssignments) oldAssignmentMap[a.WorkItemId] = a.AgentId;
        var reassigned = new List<string>();
        foreach (var assignment in newAssignments)
        {
            if (oldAssignmentMap.TryGetValue(assignment.WorkItemId, out var oldAgentId) &&
                !string.Equals(oldAgentId, assignment.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                reassigned.Add(assignment.WorkItemId);
            }
        }

        var parts = new List<string>();
        if (added.Count > 0) parts.Add($"{added.Count} tasks added");
        if (removed.Count > 0) parts.Add($"{removed.Count} tasks removed");
        if (statusChanged.Count > 0) parts.Add($"{statusChanged.Count} status changes");
        if (depsChanged.Count > 0) parts.Add($"{depsChanged.Count} dependency changes");
        if (reassigned.Count > 0) parts.Add($"{reassigned.Count} reassignments");

        return new PlanDiff
        {
            AddedWorkItemIds = added,
            RemovedWorkItemIds = removed,
            StatusChangedWorkItemIds = statusChanged,
            DependencyChangedWorkItemIds = depsChanged,
            ReassignedWorkItemIds = reassigned,
            ChangeSummary = parts.Count > 0 ? string.Join(", ", parts) : "No changes"
        };
    }

    #endregion

    #region LLM Extraction

    private async Task<T?> ExtractAsync<T>(
        string extractionName,
        string systemPrompt,
        JsonElement schema,
        string schemaName,
        string userPrompt,
        ForkedSessionResult forked,
        ConcurrentDictionary<string, DateTime> runningExtractions,
        CancellationToken cancellationToken) where T : class, new()
    {
        var startTime = DateTime.UtcNow;
        runningExtractions[extractionName] = startTime;

        try
        {
            _logger?.LogInformation("TaskWorkingAgent [{Extraction}]: Starting JSON extraction", extractionName);

            var chatOptions = new ChatOptions
            {
                Instructions = systemPrompt,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    schema: schema,
                    schemaName: schemaName,
                    schemaDescription: $"Structured {extractionName} extraction response")
            };

            var agentOptions = new ChatClientAgentOptions
            {
                ChatOptions = chatOptions,
                ChatHistoryProviderFactory = forked.HistoryProvider.CreateFactory()
            };

            var agent = new ChatClientAgent(_chatClient, agentOptions);
            var session = await agent.GetNewSessionAsync(cancellationToken);

            var response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, userPrompt),
                session,
                cancellationToken: cancellationToken);

            var elapsed = DateTime.UtcNow - startTime;
            runningExtractions.TryRemove(extractionName, out _);

            var jsonText = string.Join("", response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents)
                .OfType<TextContent>()
                .Select(t => t.Text));

            if (string.IsNullOrWhiteSpace(jsonText))
            {
                _logger?.LogWarning("TaskWorkingAgent [{Extraction}]: Empty response", extractionName);
                return new T();
            }

            var result = JsonSerializer.Deserialize<T>(jsonText, JsonOptions);

            _logger?.LogInformation("TaskWorkingAgent [{Extraction}]: Completed in {Elapsed:F1}s", extractionName, elapsed.TotalSeconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var elapsed = DateTime.UtcNow - startTime;
            runningExtractions.TryRemove(extractionName, out _);
            _logger?.LogWarning("TaskWorkingAgent [{Extraction}]: Timed out after {Elapsed:F1}s", extractionName, elapsed.TotalSeconds);
            return new T();
        }
        catch (Exception ex)
        {
            var elapsed = DateTime.UtcNow - startTime;
            runningExtractions.TryRemove(extractionName, out _);
            _logger?.LogWarning(ex, "TaskWorkingAgent [{Extraction}]: Failed after {Elapsed:F1}s - {Error}", extractionName, elapsed.TotalSeconds, ex.Message);
            return new T();
        }
    }

    #endregion

    #region Execution API

    /// <inheritdoc />
    public TaskTracking? CurrentPlan => _currentPlan;

    /// <inheritdoc />
    public ExecutionState Execution => _executionState;

    /// <inheritdoc />
    public void StartExecution()
    {
        if (_executionOptions == null)
            throw new InvalidOperationException("ExecutionOptions must be provided to use execution features.");
        if (_currentPlan == null)
            throw new InvalidOperationException("No plan exists. Call PlanAsync or PlanOrReplanAsync first.");
        if (_executionState.IsExecuting)
            return;

        _executionState.IsExecuting = true;
        _ = ExecutionLoopAsync();
        _logger?.LogInformation("Plan execution started");
    }

    /// <inheritdoc />
    public void StopExecution()
    {
        _executionState.IsExecuting = false;
    }

    /// <inheritdoc />
    public void HandleRetryTimer(string workItemId)
    {
        _executionState.PendingRetries.Remove(workItemId);
        _executionOptions?.AgentHost.UnregisterTimer($"retry-{workItemId}");
        _logger?.LogInformation("Retry timer fired for {WorkItemId}, pending retries remaining: {Count}",
            workItemId, _executionState.PendingRetries.Count);

        if (!_executionState.IsExecuting && _currentPlan != null && _executionOptions != null)
        {
            StartExecution();
        }
    }

    /// <inheritdoc />
    public async Task<TaskTracking?> PlanOrReplanAsync(
        List<TaskStatusUpdate>? statusUpdates = null,
        EffortLevel? effortLevel = null,
        string? extractionPrompt = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            TaskTracking tracking;
            if (_currentPlan != null)
            {
                _logger?.LogDebug("Replanning from v{Version}", _currentPlan.PlanVersion);
                var replanContext = new ReplanContext
                {
                    PreviousPlan = _currentPlan,
                    StatusUpdates = statusUpdates ?? [],
                    NewContext = []
                };
                tracking = await ReplanAsync(
                    replanContext,
                    availableAgents: _executionOptions?.AvailableAgents,
                    effortLevel: effortLevel,
                    extractionPrompt: extractionPrompt,
                    cancellationToken: cancellationToken);
            }
            else
            {
                tracking = await PlanAsync(
                    availableAgents: _executionOptions?.AvailableAgents,
                    effortLevel: effortLevel ?? EffortLevel.Quick,
                    extractionPrompt: extractionPrompt,
                    cancellationToken: cancellationToken);
            }
            return tracking;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in plan/replan");
            return null;
        }
    }

    /// <inheritdoc />
    public string BuildTrackingContext(bool includeExecutionState = true)
    {
        if (_currentPlan == null) return "";

        var sb = new StringBuilder();
        sb.AppendLine($"Phase: {_currentPlan.Phase}");
        sb.AppendLine($"Plan Version: v{_currentPlan.PlanVersion}");
        sb.AppendLine($"Effort Level: {_currentPlan.EffortLevel}");
        if (includeExecutionState)
            sb.AppendLine($"Execution Running: {_executionState.IsExecuting}");
        if (!string.IsNullOrWhiteSpace(_currentPlan.Summary))
            sb.AppendLine($"Summary: {_currentPlan.Summary}");

        sb.AppendLine();
        sb.AppendLine("Work Items:");
        foreach (var item in _currentPlan.AllWork)
        {
            sb.AppendLine($"  - [{item.Status.ToUpperInvariant()}] {item.Title} (assigned to: {item.Owner})");
            if (!string.IsNullOrWhiteSpace(item.Result))
                sb.AppendLine($"    Result: {item.Result}");
            if (!string.IsNullOrWhiteSpace(item.BlockedReason))
                sb.AppendLine($"    Blocked: {item.BlockedReason}");
        }

        if (_currentPlan.Blockers?.Any() == true)
        {
            sb.AppendLine();
            sb.AppendLine("Blockers:");
            foreach (var b in _currentPlan.Blockers)
                sb.AppendLine($"  - [{b.Status}] {b.Description}");
        }

        return sb.ToString();
    }

    #endregion

    #region Execution Loop

    private enum LoopAction { Continue, Break }

    private async Task ExecutionLoopAsync()
    {
        var stallCount = 0;
        var lastCompletedCount = 0;

        try
        {
            while (_executionState.IsExecuting)
            {
                if (_currentPlan == null) break;

                var (nextItem, completedCount) = FindNextExecutableItem();

                if (nextItem == null)
                {
                    var (action, newLastCompleted, newStall) = await HandleNoWorkAvailableAsync(
                        completedCount, lastCompletedCount, stallCount);
                    lastCompletedCount = newLastCompleted;
                    stallCount = newStall;
                    if (action == LoopAction.Break) break;
                    continue;
                }

                stallCount = 0;
                lastCompletedCount = completedCount;

                await DispatchAndProcessWorkItemAsync(nextItem);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Execution loop error");
        }
        finally
        {
            _executionState.IsExecuting = false;
            _logger?.LogInformation("Plan execution loop finished (pending retries: {RetryCount})", _executionState.PendingRetries.Count);
        }
    }

    private (WorkItem? item, int completedCount) FindNextExecutableItem()
    {
        var completedCount = _currentPlan!.AllWork.Count(w => w.Status == "completed");

        var completedIds = new HashSet<string>(
            _currentPlan.AllWork
                .Where(w => w.Status == "completed")
                .Select(w => w.Id),
            StringComparer.OrdinalIgnoreCase);

        var nextItem = _currentPlan.ExecutionOrder
            .Select(id => _currentPlan.AllWork.FirstOrDefault(w =>
                string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(w => w != null)
            .FirstOrDefault(w =>
                (w!.Status == "pending" || w.Status == "in_progress") &&
                !_executionState.PendingRetries.Contains(w.Id) &&
                w.DependencyIds.All(d => completedIds.Contains(d)));

        return (nextItem, completedCount);
    }

    private async Task<(LoopAction action, int lastCompletedCount, int stallCount)>
        HandleNoWorkAvailableAsync(int currentCompletedCount, int lastCompletedCount, int stallCount)
    {
        var opts = _executionOptions!;

        var hasActionableWork = _currentPlan!.AllWork.Any(w =>
            (w.Status == "pending" || w.Status == "in_progress") &&
            !_executionState.PendingRetries.Contains(w.Id));

        var hasRetryPending = _executionState.PendingRetries.Count > 0;

        if (!hasActionableWork && !hasRetryPending)
        {
            var hasFailed = _currentPlan.AllWork.Any(w => w.Status == "failed");
            var message = hasFailed
                ? "Execution complete — some tasks failed. Review the plan for details."
                : "All tasks completed!";
            if (opts.OnExecutionComplete != null)
                await opts.OnExecutionComplete(message, hasFailed);
            return (LoopAction.Break, lastCompletedCount, stallCount);
        }

        if (currentCompletedCount > lastCompletedCount)
        {
            stallCount = 0;
            lastCompletedCount = currentCompletedCount;
        }
        else
        {
            stallCount++;
            _logger?.LogDebug("Execution loop waiting — stall cycle {StallCount}/{MaxStall}, pending retries: {Retries}",
                stallCount, opts.MaxStallCycles, _executionState.PendingRetries.Count);
        }

        if (stallCount >= opts.MaxStallCycles)
        {
            var message = "Execution stalled — no forward progress after extended wait. Review the plan for blocked or failed items.";
            if (opts.OnExecutionComplete != null)
                await opts.OnExecutionComplete(message, true);
            return (LoopAction.Break, lastCompletedCount, stallCount);
        }

        await Task.Delay(opts.PollDelay);
        return (LoopAction.Continue, lastCompletedCount, stallCount);
    }

    private async Task DispatchAndProcessWorkItemAsync(WorkItem workItem)
    {
        var opts = _executionOptions!;

        var retryCount = _executionState.RetryCounts.GetValueOrDefault(workItem.Id, 0);
        if (opts.OnWorkItemStarting != null)
            await opts.OnWorkItemStarting(workItem, retryCount);

        var targetHandle = ResolveHandle(workItem.Owner ?? "");

        var dispatchMessage = opts.FormatDispatchMessage != null
            ? opts.FormatDispatchMessage(workItem)
            : await GenerateDispatchMessageAsync(workItem);

        try
        {
            var response = await opts.AgentHost.SendAndReceiveMessage(new AgentMessage
            {
                ToHandle = targetHandle,
                Message = dispatchMessage,
                Channel = "agent"
            });

            var resultText = response.Message ?? "";

            _logger?.LogInformation("Agent {Agent} responded for {WorkItem}: {MessageType} - {Result}",
                workItem.Owner, workItem.Id, response.MessageType ?? "", resultText);

            var evaluation = await ClassifyAndHandleResponseAsync(workItem, response);
            if (evaluation == null) return; // error already handled

            if (evaluation.Outcome == WorkItemOutcome.Failed)
            {
                await HandlePermanentFailure(workItem,
                    $"Agent response indicates failure: {evaluation.Summary}");
                return;
            }

            if (evaluation.Outcome == WorkItemOutcome.Completed)
            {
                await CompleteWorkItemAsync(workItem, resultText);
                return;
            }

            // NeedsInfo — enter follow-up loop
            await FollowUpLoopAsync(workItem, targetHandle, resultText, evaluation);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error executing work item {WorkItemId}", workItem.Id);
            await HandleTransientFailure(workItem, ex.Message);
        }
    }

    private async Task FollowUpLoopAsync(
        WorkItem workItem, string targetHandle, string lastResultText, WorkItemResponseEvaluation lastEvaluation)
    {
        var opts = _executionOptions!;
        var evaluation = lastEvaluation;
        var resultText = lastResultText;

        while (evaluation.Outcome == WorkItemOutcome.NeedsInfo)
        {
            var followUpCount = _executionState.FollowUpCounts.GetValueOrDefault(workItem.Id, 0) + 1;
            _executionState.FollowUpCounts[workItem.Id] = followUpCount;

            if (followUpCount > opts.MaxFollowUps)
            {
                _logger?.LogWarning("Work item {WorkItemId} exceeded max follow-ups ({Max}), marking as failed",
                    workItem.Id, opts.MaxFollowUps);
                await HandlePermanentFailure(workItem,
                    $"Agent required too many follow-ups ({followUpCount - 1}/{opts.MaxFollowUps}). Last response: {resultText}");
                return;
            }

            var followUpMessage = evaluation.FollowUpMessage
                ?? $"Please proceed with the task using reasonable assumptions. Original task: {workItem.Title}: {workItem.Description}";

            _logger?.LogInformation("Work item {WorkItemId} needs info, sending follow-up {Count}/{Max}",
                workItem.Id, followUpCount, opts.MaxFollowUps);

            if (opts.OnWorkItemFollowUp != null)
                await opts.OnWorkItemFollowUp(workItem, resultText, followUpMessage, followUpCount);

            try
            {
                var followUpResponse = await opts.AgentHost.SendAndReceiveMessage(new AgentMessage
                {
                    ToHandle = targetHandle,
                    Message = followUpMessage,
                    Channel = "agent"
                });

                resultText = followUpResponse.Message ?? "";

                evaluation = await ClassifyAndHandleResponseAsync(workItem, followUpResponse);
                if (evaluation == null) return; // error already handled
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error sending follow-up for {WorkItemId}", workItem.Id);
                await HandleTransientFailure(workItem, ex.Message);
                return;
            }
        }

        if (evaluation.Outcome == WorkItemOutcome.Failed)
        {
            await HandlePermanentFailure(workItem,
                $"Agent failed after follow-up: {evaluation.Summary}");
        }
        else
        {
            await CompleteWorkItemAsync(workItem, resultText);
        }
    }

    private async Task<WorkItemResponseEvaluation?> ClassifyAndHandleResponseAsync(
        WorkItem workItem, AgentMessage response)
    {
        var messageType = response.MessageType ?? "";

        if (messageType == "agent-error-transient")
        {
            await HandleTransientFailure(workItem, response.Message ?? "");
            return null;
        }

        if (messageType == "agent-error")
        {
            await HandlePermanentFailure(workItem, response.Message ?? "");
            return null;
        }

        return await EvaluateResponseAsync(workItem, response.Message ?? "");
    }

    private async Task CompleteWorkItemAsync(WorkItem workItem, string resultText)
    {
        var opts = _executionOptions!;

        _executionState.RetryCounts.Remove(workItem.Id);
        _executionState.FollowUpCounts.Remove(workItem.Id);

        var statusUpdates = new List<TaskStatusUpdate>
        {
            new()
            {
                WorkItemId = workItem.Id,
                NewStatus = "completed",
                Result = resultText
            }
        };

        var tracking = await PlanOrReplanAsync(statusUpdates: statusUpdates);
        if (tracking != null && opts.OnPlanUpdated != null)
            await opts.OnPlanUpdated(tracking);

        if (opts.OnWorkItemCompleted != null)
            await opts.OnWorkItemCompleted(workItem, resultText);
    }

    private async Task<string> GenerateDispatchMessageAsync(WorkItem workItem)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("WORK ITEM TO DISPATCH:");
            sb.AppendLine($"- Title: {workItem.Title}");
            sb.AppendLine($"- Description: {workItem.Description}");
            if (!string.IsNullOrEmpty(workItem.SuccessCriteria))
                sb.AppendLine($"- Success Criteria: {workItem.SuccessCriteria}");

            if (_currentPlan != null)
            {
                // Include results from direct dependencies first
                var dependencyResults = _currentPlan.AllWork
                    .Where(w => workItem.DependencyIds.Contains(w.Id, StringComparer.OrdinalIgnoreCase)
                                && w.Status == "completed"
                                && !string.IsNullOrEmpty(w.Result))
                    .ToList();

                if (dependencyResults.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("RESULTS FROM DEPENDENCY TASKS (this work item depends on these):");
                    foreach (var dep in dependencyResults)
                        sb.AppendLine($"- {dep.Title}: {dep.Result}");
                }

                // Include other completed work items for broader context
                var otherCompleted = _currentPlan.AllWork
                    .Where(w => w.Status == "completed"
                                && !string.IsNullOrEmpty(w.Result)
                                && !workItem.DependencyIds.Contains(w.Id, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (otherCompleted.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("OTHER COMPLETED WORK (may contain relevant context):");
                    foreach (var item in otherCompleted)
                        sb.AppendLine($"- {item.Title}: {item.Result}");
                }

                if (!string.IsNullOrEmpty(_currentPlan.Summary))
                {
                    sb.AppendLine();
                    sb.AppendLine($"OVERALL PLAN CONTEXT: {_currentPlan.Summary}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Generate the dispatch message for the worker agent.");

            var chatOptions = new ChatOptions
            {
                Instructions = DispatchMessageSystemPrompt
            };

            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, sb.ToString())],
                chatOptions);

            var messageText = string.Join("", response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents)
                .OfType<TextContent>()
                .Select(t => t.Text));

            if (!string.IsNullOrWhiteSpace(messageText))
            {
                _logger?.LogInformation("Generated dispatch message for {WorkItemId} ({Length} chars)",
                    workItem.Id, messageText.Length);
                return messageText;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to generate dispatch message for {WorkItemId}, falling back to default",
                workItem.Id);
        }

        // Fallback to simple format
        return $"{workItem.Title}: {workItem.Description}";
    }

    private async Task<WorkItemResponseEvaluation> EvaluateResponseAsync(WorkItem workItem, string agentResponse)
    {
        try
        {
            // Build context from completed work items
            var completedContext = "";
            var downstreamContext = "";
            if (_currentPlan != null)
            {
                var completedItems = _currentPlan.AllWork
                    .Where(w => w.Status == "completed" && !string.IsNullOrEmpty(w.Result))
                    .ToList();
                if (completedItems.Count > 0)
                {
                    var contextParts = completedItems.Select(w => $"- {w.Title}: {w.Result}");
                    completedContext = $"\n\nCONTEXT FROM COMPLETED WORK ITEMS:\n{string.Join("\n", contextParts)}";
                }

                // Find downstream work items that depend on this one
                var downstreamItems = _currentPlan.AllWork
                    .Where(w => w.DependencyIds.Contains(workItem.Id, StringComparer.OrdinalIgnoreCase)
                                && w.Status is "pending" or "blocked")
                    .ToList();
                if (downstreamItems.Count > 0)
                {
                    var downstreamParts = downstreamItems.Select(w =>
                        $"- {w.Title}: {w.Description}" +
                        (string.IsNullOrEmpty(w.SuccessCriteria) ? "" : $" (needs: {w.SuccessCriteria})"));
                    downstreamContext = $"\n\nDOWNSTREAM WORK ITEMS THAT DEPEND ON THIS RESULT:\n{string.Join("\n", downstreamParts)}";
                }
            }

            var userPrompt = $"""
                WORK ITEM:
                - Title: {workItem.Title}
                - Description: {workItem.Description}
                - Success Criteria: {workItem.SuccessCriteria ?? "N/A"}

                AGENT RESPONSE:
                {agentResponse}
                {completedContext}
                {downstreamContext}

                Evaluate whether this response truly completes the work item with actual deliverable data
                that downstream tasks can consume. A claim of completion without the actual output is NOT complete.
                """;

            var schema = AIJsonUtilities.CreateJsonSchema(typeof(ResponseEvaluationResponse));
            var chatOptions = new ChatOptions
            {
                Instructions = ResponseEvaluationSystemPrompt,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    schema: schema,
                    schemaName: "ResponseEvaluationResponse",
                    schemaDescription: "Evaluation of agent response against work item requirements")
            };

            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, userPrompt)],
                chatOptions);

            var jsonText = string.Join("", response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents)
                .OfType<TextContent>()
                .Select(t => t.Text));

            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                var evalResult = JsonSerializer.Deserialize<ResponseEvaluationResponse>(jsonText, JsonOptions);
                if (evalResult != null)
                {
                    var outcome = evalResult.Outcome?.ToLowerInvariant() switch
                    {
                        "needsinfo" => WorkItemOutcome.NeedsInfo,
                        "failed" => WorkItemOutcome.Failed,
                        _ => WorkItemOutcome.Completed
                    };

                    _logger?.LogInformation("Response evaluation for {WorkItemId}: {Outcome} — {Summary}",
                        workItem.Id, outcome, evalResult.Summary);

                    return new WorkItemResponseEvaluation
                    {
                        Outcome = outcome,
                        Summary = evalResult.Summary,
                        FollowUpMessage = evalResult.FollowUpMessage
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Response evaluation failed for {WorkItemId}, treating as completed", workItem.Id);
        }

        // Default to completed if evaluation fails
        return new WorkItemResponseEvaluation
        {
            Outcome = WorkItemOutcome.Completed,
            Summary = "Evaluation unavailable — defaulting to completed"
        };
    }

    private async Task HandleTransientFailure(WorkItem workItem, string errorMessage)
    {
        var opts = _executionOptions!;
        var retryCount = _executionState.RetryCounts.GetValueOrDefault(workItem.Id, 0) + 1;
        _executionState.RetryCounts[workItem.Id] = retryCount;

        if (retryCount <= opts.MaxRetries)
        {
            _executionState.PendingRetries.Add(workItem.Id);
            workItem.Attempts = retryCount;

            opts.AgentHost.RegisterTimer(
                $"retry-{workItem.Id}",
                "retry-workitem",
                workItem.Id,
                dueTime: opts.RetryDelay,
                period: Timeout.InfiniteTimeSpan);

            _logger?.LogInformation("Scheduled retry {Attempt}/{Max} for {WorkItemId} in {Delay}s",
                retryCount, opts.MaxRetries, workItem.Id, opts.RetryDelay.TotalSeconds);

            if (opts.OnWorkItemFailed != null)
                await opts.OnWorkItemFailed(workItem, errorMessage, false);
        }
        else
        {
            await HandlePermanentFailure(workItem,
                $"Failed after {retryCount - 1} retries: {errorMessage}");
        }
    }

    private async Task HandlePermanentFailure(WorkItem workItem, string errorMessage)
    {
        var opts = _executionOptions!;
        _executionState.RetryCounts.Remove(workItem.Id);
        _executionState.PendingRetries.Remove(workItem.Id);

        _logger?.LogWarning("Permanent failure for {WorkItemId}: {Error}", workItem.Id, errorMessage);

        var statusUpdates = new List<TaskStatusUpdate>
        {
            new()
            {
                WorkItemId = workItem.Id,
                NewStatus = "failed",
                Result = errorMessage
            }
        };

        var tracking = await PlanOrReplanAsync(statusUpdates: statusUpdates);
        if (tracking != null && opts.OnPlanUpdated != null)
            await opts.OnPlanUpdated(tracking);

        if (opts.OnWorkItemFailed != null)
            await opts.OnWorkItemFailed(workItem, errorMessage, true);
    }

    private string ResolveHandle(string agentId)
    {
        var myHandle = _executionOptions!.AgentHost.GetHandle();
        if (_executionOptions.ResolveAgentHandle != null)
            return _executionOptions.ResolveAgentHandle(myHandle, agentId);
        var prefix = myHandle.Contains(':') ? myHandle[..(myHandle.IndexOf(':') + 1)] : "";
        return $"{prefix}{agentId}";
    }

    #endregion

    #region Utilities

    private static string BuildAgentsContext(IReadOnlyList<AvailableAgent>? availableAgents)
    {
        if (availableAgents == null || availableAgents.Count == 0)
            return "";

        var agentsList = string.Join("\n", availableAgents.Select(a =>
            $"- {a.Id}: {a.Name} - {a.Description} (capabilities: {string.Join(", ", a.Capabilities)})"));
        return $"\n\nAvailable agents:\n{agentsList}";
    }

    private async Task LogProgressAsync(
        ConcurrentDictionary<string, DateTime> runningExtractions,
        DateTime overallStart,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (runningExtractions.Count > 0)
                {
                    var stillRunning = string.Join(", ", runningExtractions.Keys);
                    var totalElapsed = DateTime.UtcNow - overallStart;
                    _logger?.LogInformation("TaskWorkingAgent: Still running after {Elapsed:F0}s: [{Running}]", totalElapsed.TotalSeconds, stillRunning);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion
}

/// <summary>
/// Deterministic plan validator. Provides pure-code structural validation and fixes.
/// </summary>
public static class PlanValidator
{
    /// <summary>
    /// Validates and fixes a TaskTracking plan in place.
    /// Removes orphan references, breaks cycles, and recomputes execution order.
    /// </summary>
    public static void Validate(TaskTracking tracking)
    {
        // Deduplicate work items by ID — LLM may return duplicates (keep last occurrence)
        var deduped = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in tracking.AllWork) deduped[w.Id] = w;
        if (deduped.Count < tracking.AllWork.Count)
            tracking.AllWork = [.. deduped.Values];

        var workItemIds = new HashSet<string>(
            tracking.AllWork.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);

        // Fix orphan dependency references
        foreach (var item in tracking.AllWork)
        {
            item.DependencyIds = item.DependencyIds
                .Where(id => workItemIds.Contains(id))
                .ToList();

            // Fix orphan parentIds
            if (item.ParentId != null && !workItemIds.Contains(item.ParentId))
                item.ParentId = null;
        }

        // Fix blocker references
        foreach (var blocker in tracking.Blockers)
        {
            blocker.BlocksWorkItemIds = blocker.BlocksWorkItemIds
                .Where(id => workItemIds.Contains(id))
                .ToList();
        }

        // Fix agent assignment references
        tracking.AgentAssignments = tracking.AgentAssignments
            .Where(a => workItemIds.Contains(a.WorkItemId))
            .ToList();

        // Break cycles if any
        BreakCycles(tracking.AllWork);

        // Recompute execution order deterministically (overrides LLM ordering)
        tracking.ExecutionOrder = ComputeExecutionOrder(tracking.AllWork);

        // Recompute critical path deterministically
        tracking.CriticalPath = ComputeCriticalPath(tracking.AllWork);

        // Set ExecutionOrder on individual work items
        for (int i = 0; i < tracking.ExecutionOrder.Count; i++)
        {
            var item = tracking.AllWork.FirstOrDefault(w =>
                string.Equals(w.Id, tracking.ExecutionOrder[i], StringComparison.OrdinalIgnoreCase));
            if (item != null)
                item.ExecutionOrder = i + 1;
        }
    }

    /// <summary>
    /// Computes execution order using Kahn's algorithm (topological sort) with priority tie-breaking.
    /// </summary>
    public static List<string> ComputeExecutionOrder(List<WorkItem> workItems)
    {
        if (workItems.Count == 0)
            return [];

        var idSet = new HashSet<string>(workItems.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in workItems)
        {
            inDegree[item.Id] = 0;
            adjacency[item.Id] = [];
        }

        foreach (var item in workItems)
        {
            foreach (var depId in item.DependencyIds)
            {
                if (idSet.Contains(depId))
                {
                    adjacency[depId].Add(item.Id);
                    inDegree[item.Id]++;
                }
            }
        }

        // Priority ordering for tie-breaking: completed first, then by priority
        var priorityRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["low"] = 3
        };
        var statusRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["completed"] = 0, ["in_progress"] = 1, ["pending"] = 2, ["blocked"] = 3, ["failed"] = 4, ["cancelled"] = 5
        };

        var itemMap = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in workItems) itemMap[w.Id] = w;

        // Use a sorted set with priority comparison for Kahn's
        var ready = new SortedSet<string>(Comparer<string>.Create((a, b) =>
        {
            var itemA = itemMap[a];
            var itemB = itemMap[b];
            var statusA = statusRank.GetValueOrDefault(itemA.Status, 99);
            var statusB = statusRank.GetValueOrDefault(itemB.Status, 99);
            if (statusA != statusB) return statusA.CompareTo(statusB);
            var prioA = priorityRank.GetValueOrDefault(itemA.Priority ?? "medium", 2);
            var prioB = priorityRank.GetValueOrDefault(itemB.Priority ?? "medium", 2);
            if (prioA != prioB) return prioA.CompareTo(prioB);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }));

        foreach (var item in workItems)
        {
            if (inDegree[item.Id] == 0)
                ready.Add(item.Id);
        }

        var result = new List<string>();
        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            result.Add(current);

            foreach (var neighbor in adjacency[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    ready.Add(neighbor);
            }
        }

        // If there are remaining items (cycles were not fully broken), add them
        foreach (var item in workItems)
        {
            if (!result.Contains(item.Id))
                result.Add(item.Id);
        }

        return result;
    }

    /// <summary>
    /// Computes the critical path (longest dependency chain) via DFS.
    /// </summary>
    public static List<string> ComputeCriticalPath(List<WorkItem> workItems)
    {
        if (workItems.Count == 0)
            return [];

        var idSet = new HashSet<string>(workItems.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var predecessors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in workItems)
        {
            adjacency[item.Id] = [];
            predecessors[item.Id] = [];
        }

        foreach (var item in workItems)
        {
            foreach (var depId in item.DependencyIds)
            {
                if (idSet.Contains(depId))
                {
                    adjacency[depId].Add(item.Id);
                    predecessors[item.Id].Add(depId);
                }
            }
        }

        // Find longest path using DFS with memoization
        var memo = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var longestPath = new List<string>();

        foreach (var item in workItems)
        {
            var path = GetLongestPath(item.Id, adjacency, memo);
            if (path.Count > longestPath.Count)
                longestPath = path;
        }

        return longestPath;
    }

    private static List<string> GetLongestPath(
        string nodeId,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, List<string>> memo)
    {
        if (memo.TryGetValue(nodeId, out var cached))
            return cached;

        var bestDownstream = new List<string>();
        if (adjacency.TryGetValue(nodeId, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                var downstream = GetLongestPath(neighbor, adjacency, memo);
                if (downstream.Count > bestDownstream.Count)
                    bestDownstream = downstream;
            }
        }

        var result = new List<string> { nodeId };
        result.AddRange(bestDownstream);
        memo[nodeId] = result;
        return result;
    }

    /// <summary>
    /// Detects and breaks dependency cycles by removing the edge that creates each cycle.
    /// </summary>
    public static void BreakCycles(List<WorkItem> workItems)
    {
        var idSet = new HashSet<string>(workItems.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
        var itemMap = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in workItems) itemMap[w.Id] = w;

        while (HasCycles(workItems))
        {
            // Find a cycle using DFS and break it
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var broken = false;

            foreach (var item in workItems)
            {
                if (!visited.Contains(item.Id))
                {
                    broken = DfsBreakCycle(item.Id, visited, inStack, itemMap, idSet);
                    if (broken) break;
                }
            }

            if (!broken) break; // Safety: avoid infinite loop
        }
    }

    private static bool DfsBreakCycle(
        string nodeId,
        HashSet<string> visited,
        HashSet<string> inStack,
        Dictionary<string, WorkItem> itemMap,
        HashSet<string> idSet)
    {
        visited.Add(nodeId);
        inStack.Add(nodeId);

        if (itemMap.TryGetValue(nodeId, out var item))
        {
            foreach (var depId in item.DependencyIds.ToList())
            {
                if (!idSet.Contains(depId)) continue;

                if (inStack.Contains(depId))
                {
                    // Found cycle: remove this edge
                    item.DependencyIds.Remove(depId);
                    return true;
                }

                if (!visited.Contains(depId))
                {
                    if (DfsBreakCycle(depId, visited, inStack, itemMap, idSet))
                        return true;
                }
            }
        }

        inStack.Remove(nodeId);
        return false;
    }

    /// <summary>
    /// Checks if the dependency graph has cycles.
    /// </summary>
    public static bool HasCycles(List<WorkItem> workItems)
    {
        var idSet = new HashSet<string>(workItems.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemMap = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in workItems) itemMap[w.Id] = w;

        foreach (var item in workItems)
        {
            if (!visited.Contains(item.Id))
            {
                if (DfsHasCycle(item.Id, visited, inStack, itemMap, idSet))
                    return true;
            }
        }

        return false;
    }

    private static bool DfsHasCycle(
        string nodeId,
        HashSet<string> visited,
        HashSet<string> inStack,
        Dictionary<string, WorkItem> itemMap,
        HashSet<string> idSet)
    {
        visited.Add(nodeId);
        inStack.Add(nodeId);

        if (itemMap.TryGetValue(nodeId, out var item))
        {
            foreach (var depId in item.DependencyIds)
            {
                if (!idSet.Contains(depId)) continue;

                if (inStack.Contains(depId))
                    return true;

                if (!visited.Contains(depId) && DfsHasCycle(depId, visited, inStack, itemMap, idSet))
                    return true;
            }
        }

        inStack.Remove(nodeId);
        return false;
    }
}
