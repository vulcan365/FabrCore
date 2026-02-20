using Microsoft.Agents.AI;

namespace Fabr.Sdk;

/// <summary>
/// Interface for plan-and-replan task tracking from conversations.
/// Creates structured task plans and iteratively refines them as work progresses.
/// </summary>
public interface ITaskWorkingAgent
{
    /// <summary>
    /// Create an initial task plan from the conversation.
    /// </summary>
    /// <param name="availableAgents">List of available agents that can be assigned to tasks.</param>
    /// <param name="extractionPrompt">Optional prompt to guide extraction focus.</param>
    /// <param name="effortLevel">Controls planning depth: Quick (minimal), Standard (balanced), Thorough (comprehensive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created TaskTracking plan.</returns>
    Task<TaskTracking> PlanAsync(
        IReadOnlyList<AvailableAgent>? availableAgents = null,
        string? extractionPrompt = null,
        EffortLevel effortLevel = EffortLevel.Quick,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing plan based on new information and status changes.
    /// </summary>
    /// <param name="replanContext">Context including previous plan, status updates, and new information.</param>
    /// <param name="availableAgents">List of available agents that can be assigned to tasks.</param>
    /// <param name="extractionPrompt">Optional prompt to guide extraction focus.</param>
    /// <param name="effortLevel">Controls planning depth. Null inherits from the previous plan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated TaskTracking plan with incremented version and diff.</returns>
    Task<TaskTracking> ReplanAsync(
        ReplanContext replanContext,
        IReadOnlyList<AvailableAgent>? availableAgents = null,
        string? extractionPrompt = null,
        EffortLevel? effortLevel = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The current plan, updated after each PlanAsync/ReplanAsync/PlanOrReplanAsync call.
    /// </summary>
    TaskTracking? CurrentPlan { get; }

    /// <summary>
    /// Read-only view of execution state (IsExecuting, retry counts, pending retries).
    /// </summary>
    ExecutionState Execution { get; }

    /// <summary>
    /// Starts the execution loop. Requires ExecutionOptions to have been provided and a plan to exist.
    /// </summary>
    void StartExecution();

    /// <summary>
    /// Stops the execution loop.
    /// </summary>
    void StopExecution();

    /// <summary>
    /// Handles a retry timer firing for a specific work item.
    /// Removes from pending retries, unregisters timer, and restarts execution if stopped.
    /// </summary>
    /// <param name="workItemId">The work item ID to retry.</param>
    void HandleRetryTimer(string workItemId);

    /// <summary>
    /// Convenience method that calls PlanAsync or ReplanAsync depending on whether a plan already exists.
    /// Stores the result in CurrentPlan.
    /// </summary>
    Task<TaskTracking?> PlanOrReplanAsync(
        List<TaskStatusUpdate>? statusUpdates = null,
        EffortLevel? effortLevel = null,
        string? extractionPrompt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a human-readable text representation of the current plan for context injection.
    /// </summary>
    /// <param name="includeExecutionState">Whether to include execution state (IsExecuting, retries).</param>
    string BuildTrackingContext(bool includeExecutionState = true);
}

/// <summary>
/// Factory for creating TaskWorkingAgent instances.
/// Register this as a singleton in DI to create agents per-session.
/// </summary>
public interface ITaskWorkingAgentFactory
{
    /// <summary>
    /// Creates a TaskWorkingAgent for the specified session.
    /// </summary>
    /// <param name="session">The session to analyze for task tracking.</param>
    /// <returns>A new TaskWorkingAgent instance.</returns>
    ITaskWorkingAgent Create(AgentSession session);
}
