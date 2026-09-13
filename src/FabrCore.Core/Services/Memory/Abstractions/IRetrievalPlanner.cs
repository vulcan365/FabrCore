using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Decides how a recall request should be executed. Given the query and the agent's
/// current hot-layer index, returns a <see cref="RetrievalPlan"/> that the memory service
/// executes step-by-step.
///
/// <para>
/// LinkedIn's Cognitive Memory Agent frames retrieval as "a reasoning process, not a single
/// search operation" — the planner is the surface where that reasoning happens. For trivial
/// queries the planner returns a minimal plan (hot-index only, no LLM calls). For complex
/// queries it composes a deeper plan including graph expansion and archive search.
/// </para>
/// </summary>
public interface IRetrievalPlanner
{
    /// <summary>
    /// Produce a retrieval plan for the given query.
    /// </summary>
    /// <param name="query">The user query driving the recall.</param>
    /// <param name="hotIndex">The agent's current hot-layer index, for heuristic overlap checks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A plan the memory service can execute.</returns>
    Task<RetrievalPlan> CreatePlanAsync(
        string query,
        MemoryIndex hotIndex,
        CancellationToken ct = default);
}
