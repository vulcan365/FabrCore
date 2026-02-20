using System.ComponentModel;

namespace FabrCore.Sdk;

/// <summary>
/// Controls the depth and breadth of task planning.
/// Quick: minimal plan, just the core task. Standard: core plus verification/follow-up. Thorough: full coverage with monitoring, contingencies, notifications.
/// </summary>
public enum EffortLevel
{
    Quick,
    Standard,
    Thorough
}

/// <summary>
/// Represents a single work item or task being tracked.
/// </summary>
public class WorkItem
{
    /// <summary>
    /// Unique identifier for the work item.
    /// </summary>
    [Description("Unique identifier for the work item")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Short title or name of the work item.
    /// </summary>
    [Description("Short title or name of the work item")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of what needs to be done or was done.
    /// </summary>
    [Description("Detailed description of what needs to be done or was done")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Status: pending, in_progress, completed, blocked, cancelled, failed.
    /// </summary>
    [Description("Status: pending, in_progress, completed, blocked, cancelled, failed")]
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Priority: critical, high, medium, low.
    /// </summary>
    [Description("Priority: critical, high, medium, low")]
    public string? Priority { get; set; }

    /// <summary>
    /// Who is responsible for this work item.
    /// </summary>
    [Description("Who is responsible for this work item")]
    public string? Owner { get; set; }

    /// <summary>
    /// The result or outcome if completed.
    /// </summary>
    [Description("The result or outcome if completed")]
    public string? Result { get; set; }

    /// <summary>
    /// Reason if blocked, cancelled, or failed.
    /// </summary>
    [Description("Reason if blocked, cancelled, or failed")]
    public string? BlockedReason { get; set; }

    /// <summary>
    /// Parent work item ID if this is a subtask.
    /// </summary>
    [Description("Parent work item ID if this is a subtask")]
    public string? ParentId { get; set; }

    /// <summary>
    /// Child work items that are part of this task.
    /// </summary>
    [Description("Child work items that are part of this task")]
    public List<WorkItem> SubTasks { get; set; } = [];

    /// <summary>
    /// IDs of work items that must complete before this can start.
    /// </summary>
    [Description("IDs of work items that must complete before this can start")]
    public List<string> DependencyIds { get; set; } = [];

    /// <summary>
    /// What defines "done" for this task - the success criteria.
    /// </summary>
    [Description("What defines 'done' for this task - the success criteria")]
    public string? SuccessCriteria { get; set; }

    /// <summary>
    /// Number of times this task has been attempted.
    /// </summary>
    [Description("Number of times this task has been attempted")]
    public int Attempts { get; set; } = 0;

    /// <summary>
    /// Estimated complexity: simple, medium, complex.
    /// </summary>
    [Description("Estimated complexity: simple (single step), medium (2-5 steps), complex (>5 steps)")]
    public string? EstimatedComplexity { get; set; }

    /// <summary>
    /// Position in execution sequence (1-based).
    /// </summary>
    [Description("Position in execution sequence (1-based)")]
    public int? ExecutionOrder { get; set; }
}

/// <summary>
/// Represents an obstacle or blocker preventing progress.
/// </summary>
public class Blocker
{
    /// <summary>
    /// Unique identifier for the blocker.
    /// </summary>
    [Description("Unique identifier for the blocker")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Description of what is blocking progress.
    /// </summary>
    [Description("Description of what is blocking progress")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// IDs of work items blocked by this blocker.
    /// </summary>
    [Description("IDs of work items blocked by this blocker")]
    public List<string> BlocksWorkItemIds { get; set; } = [];

    /// <summary>
    /// Severity: critical, major, minor.
    /// </summary>
    [Description("Severity: critical, major, minor")]
    public string? Severity { get; set; }

    /// <summary>
    /// Proposed resolution or workaround.
    /// </summary>
    [Description("Proposed resolution or workaround")]
    public string? Resolution { get; set; }

    /// <summary>
    /// Status: active, resolved.
    /// </summary>
    [Description("Status: active, resolved")]
    public string Status { get; set; } = "active";
}

/// <summary>
/// Represents an available agent that can perform tasks.
/// </summary>
public class AvailableAgent
{
    /// <summary>
    /// Unique identifier for the agent.
    /// </summary>
    [Description("Unique identifier for the agent")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the agent.
    /// </summary>
    [Description("Display name of the agent")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this agent can do.
    /// </summary>
    [Description("Description of what this agent can do")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// List of capabilities this agent can perform.
    /// </summary>
    [Description("List of capabilities this agent can perform")]
    public List<string> Capabilities { get; set; } = [];
}

/// <summary>
/// Represents an assignment of an agent to a work item.
/// </summary>
public class AgentAssignment
{
    /// <summary>
    /// The agent ID being assigned.
    /// </summary>
    [Description("The agent ID being assigned")]
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// The work item ID this agent is assigned to.
    /// </summary>
    [Description("The work item ID this agent is assigned to")]
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>
    /// Why this agent is suitable for this task.
    /// </summary>
    [Description("Why this agent is suitable for this task")]
    public string Rationale { get; set; } = string.Empty;

    /// <summary>
    /// Specific capability being used for this assignment.
    /// </summary>
    [Description("Specific capability being used for this assignment")]
    public string Capability { get; set; } = string.Empty;
}

/// <summary>
/// Represents a strategic change or pivot in approach.
/// </summary>
public class StrategyPivot
{
    /// <summary>
    /// Unique identifier for the pivot.
    /// </summary>
    [Description("Unique identifier for the pivot")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The previous approach or strategy.
    /// </summary>
    [Description("The previous approach or strategy")]
    public string FromApproach { get; set; } = string.Empty;

    /// <summary>
    /// The new approach being taken.
    /// </summary>
    [Description("The new approach being taken")]
    public string ToApproach { get; set; } = string.Empty;

    /// <summary>
    /// Reason for the pivot or change.
    /// </summary>
    [Description("Reason for the pivot or change")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Related work item IDs affected by this pivot.
    /// </summary>
    [Description("Related work item IDs affected by this pivot")]
    public List<string> AffectedWorkItemIds { get; set; } = [];
}

/// <summary>
/// Represents task tracking information extracted from a conversation.
/// Captures what has been done, what needs to be done, and why.
/// </summary>
public class TaskTracking
{
    /// <summary>
    /// Brief summary of overall work status, including the objective and rationale.
    /// </summary>
    [Description("Brief summary of overall work status, including the objective and rationale")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// All work items being tracked (completed, in progress, pending, etc.).
    /// </summary>
    [Description("All work items being tracked")]
    public List<WorkItem> AllWork { get; set; } = [];

    /// <summary>
    /// Current blockers preventing progress (includes external dependencies).
    /// </summary>
    [Description("Current blockers preventing progress (includes external dependencies)")]
    public List<Blocker> Blockers { get; set; } = [];

    /// <summary>
    /// Agent assignments mapping agents to work items.
    /// </summary>
    [Description("Agent assignments mapping agents to work items")]
    public List<AgentAssignment> AgentAssignments { get; set; } = [];

    /// <summary>
    /// Current conversation phase: planning, execution, recovery, complete.
    /// </summary>
    [Description("Current conversation phase: planning, execution, recovery, complete")]
    public string Phase { get; set; } = "planning";

    /// <summary>
    /// Strategic pivots or approach changes detected in the conversation.
    /// </summary>
    [Description("Strategic pivots or approach changes detected in the conversation")]
    public List<StrategyPivot> StrategyPivots { get; set; } = [];

    /// <summary>
    /// Ordered work item IDs for execution.
    /// </summary>
    [Description("Ordered work item IDs for execution")]
    public List<string> ExecutionOrder { get; set; } = [];

    /// <summary>
    /// Longest dependency chain (bottleneck work item IDs).
    /// </summary>
    [Description("Longest dependency chain (bottleneck work item IDs)")]
    public List<string> CriticalPath { get; set; } = [];

    /// <summary>
    /// Explains the overall strategy and key decisions.
    /// </summary>
    [Description("Explains the overall strategy and key decisions")]
    public string? PlanRationale { get; set; }

    /// <summary>
    /// The effort level used to generate this plan.
    /// </summary>
    [Description("The effort level used to generate this plan: Quick, Standard, or Thorough")]
    public EffortLevel EffortLevel { get; set; } = EffortLevel.Quick;

    /// <summary>
    /// Plan version number. Starts at 1, increments on replan.
    /// </summary>
    [Description("Plan version number, starts at 1, increments on replan")]
    public int PlanVersion { get; set; } = 1;

    /// <summary>
    /// When the plan was created or last updated.
    /// </summary>
    [Description("When the plan was created or last updated")]
    public DateTime PlannedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// What changed in the most recent replan.
    /// </summary>
    [Description("What changed in the most recent replan")]
    public PlanDiff? LastReplanDiff { get; set; }
}

/// <summary>
/// Tracks changes between plan versions.
/// </summary>
public class PlanDiff
{
    /// <summary>
    /// IDs of work items added in the replan.
    /// </summary>
    [Description("IDs of work items added in the replan")]
    public List<string> AddedWorkItemIds { get; set; } = [];

    /// <summary>
    /// IDs of work items removed in the replan.
    /// </summary>
    [Description("IDs of work items removed in the replan")]
    public List<string> RemovedWorkItemIds { get; set; } = [];

    /// <summary>
    /// IDs of work items whose status changed.
    /// </summary>
    [Description("IDs of work items whose status changed")]
    public List<string> StatusChangedWorkItemIds { get; set; } = [];

    /// <summary>
    /// IDs of work items whose dependencies changed.
    /// </summary>
    [Description("IDs of work items whose dependencies changed")]
    public List<string> DependencyChangedWorkItemIds { get; set; } = [];

    /// <summary>
    /// IDs of work items that were reassigned to different agents.
    /// </summary>
    [Description("IDs of work items that were reassigned to different agents")]
    public List<string> ReassignedWorkItemIds { get; set; } = [];

    /// <summary>
    /// Human-readable summary of what changed.
    /// </summary>
    [Description("Human-readable summary of what changed")]
    public string ChangeSummary { get; set; } = string.Empty;
}

/// <summary>
/// Input context for replanning an existing plan.
/// </summary>
public class ReplanContext
{
    /// <summary>
    /// The previous plan to update.
    /// </summary>
    public TaskTracking PreviousPlan { get; set; } = new();

    /// <summary>
    /// Status updates for work items (e.g., "Task X completed with result Y").
    /// </summary>
    public List<TaskStatusUpdate> StatusUpdates { get; set; } = [];

    /// <summary>
    /// New requirements or information to incorporate.
    /// </summary>
    public List<string> NewContext { get; set; } = [];
}

/// <summary>
/// Configuration for the execution loop. All fields have sensible defaults except AgentHost which is required.
/// </summary>
public class ExecutionOptions
{
    /// <summary>
    /// The agent host for sending messages and managing timers.
    /// </summary>
    public required IFabrCoreAgentHost AgentHost { get; init; }

    /// <summary>
    /// Available agents that can be assigned to tasks.
    /// </summary>
    public IReadOnlyList<AvailableAgent> AvailableAgents { get; init; } = [];

    /// <summary>
    /// Maximum number of retries for transient failures.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Delay between retry attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay between polling cycles when waiting for work to become available.
    /// </summary>
    public TimeSpan PollDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of polling cycles with no progress before giving up.
    /// </summary>
    public int MaxStallCycles { get; init; } = 10;

    /// <summary>
    /// Maximum number of follow-up messages per work item when the agent needs more info.
    /// After this limit, the work item is marked as failed.
    /// </summary>
    public int MaxFollowUps { get; init; } = 3;

    /// <summary>
    /// Called when a work item is about to be dispatched. Parameters: (workItem, retryCount).
    /// </summary>
    public Func<WorkItem, int, Task>? OnWorkItemStarting { get; init; }

    /// <summary>
    /// Called when a work item completes successfully. Parameters: (workItem, result).
    /// </summary>
    public Func<WorkItem, string, Task>? OnWorkItemCompleted { get; init; }

    /// <summary>
    /// Called when a work item fails. Parameters: (workItem, error, isPermanent).
    /// </summary>
    public Func<WorkItem, string, bool, Task>? OnWorkItemFailed { get; init; }

    /// <summary>
    /// Called when an agent's response requires a follow-up (NeedsInfo).
    /// Parameters: (workItem, agentResponse, followUpMessage, followUpCount).
    /// </summary>
    public Func<WorkItem, string, string, int, Task>? OnWorkItemFollowUp { get; init; }

    /// <summary>
    /// Called after the plan is updated (replan). Parameters: (updatedTracking).
    /// </summary>
    public Func<TaskTracking, Task>? OnPlanUpdated { get; init; }

    /// <summary>
    /// Called when execution completes. Parameters: (message, hasFailures).
    /// </summary>
    public Func<string, bool, Task>? OnExecutionComplete { get; init; }

    /// <summary>
    /// Resolves a target agent handle from the current agent's handle and the agent ID.
    /// Parameters: (myHandle, agentId) => targetHandle.
    /// Default behavior extracts the prefix from myHandle (e.g., "session:") and prepends it to agentId.
    /// </summary>
    public Func<string, string, string>? ResolveAgentHandle { get; init; }

    /// <summary>
    /// Formats the message body to dispatch to a worker agent. Parameters: (workItem) => message body.
    /// Default: "{Title}: {Description}".
    /// </summary>
    public Func<WorkItem, string>? FormatDispatchMessage { get; init; }
}

/// <summary>
/// The outcome of evaluating an agent's response against the work item's requirements.
/// </summary>
public enum WorkItemOutcome
{
    /// <summary>The agent completed the work item.</summary>
    Completed,
    /// <summary>The agent needs more information before it can complete the work.</summary>
    NeedsInfo,
    /// <summary>The agent's response indicates the work item failed.</summary>
    Failed
}

/// <summary>
/// Result of evaluating an agent's response against a work item.
/// </summary>
public class WorkItemResponseEvaluation
{
    /// <summary>Whether the work item was completed, needs info, or failed.</summary>
    [Description("Outcome: completed, needsInfo, or failed")]
    public WorkItemOutcome Outcome { get; set; }

    /// <summary>Brief explanation of the evaluation decision.</summary>
    [Description("Brief explanation of why this outcome was chosen")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// When outcome is NeedsInfo, a follow-up message to send back to the agent
    /// incorporating relevant context from other completed work items.
    /// </summary>
    [Description("Follow-up message to send to the agent when it needs more information")]
    public string? FollowUpMessage { get; set; }
}

/// <summary>
/// Read-only view of execution state for callers.
/// </summary>
public class ExecutionState
{
    /// <summary>
    /// Whether the execution loop is currently running.
    /// </summary>
    public bool IsExecuting { get; internal set; }

    /// <summary>
    /// Retry counts per work item ID.
    /// </summary>
    internal readonly Dictionary<string, int> RetryCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Work item IDs that are awaiting retry timer.
    /// </summary>
    internal readonly HashSet<string> PendingRetries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Follow-up counts per work item ID (how many times we've re-dispatched after NeedsInfo).
    /// </summary>
    internal readonly Dictionary<string, int> FollowUpCounts = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a status change for a work item.
/// </summary>
public class TaskStatusUpdate
{
    /// <summary>
    /// The work item ID to update.
    /// </summary>
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>
    /// The new status (e.g., completed, failed, blocked).
    /// </summary>
    public string NewStatus { get; set; } = string.Empty;

    /// <summary>
    /// The result or outcome of the status change.
    /// </summary>
    public string? Result { get; set; }
}
