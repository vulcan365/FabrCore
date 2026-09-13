namespace FabrCore.Services.Memory.Models;

/// <summary>
/// Memory classification types for general-purpose agents.
/// Types reflect what the memory IS, not where it came from.
/// </summary>
public enum MemoryType
{
    /// <summary>
    /// Verified truths, domain knowledge, system behaviors, established states.
    /// Facts are stable — they rarely change and other memories link to them.
    /// </summary>
    Fact,

    /// <summary>
    /// Business rules, constraints, policies, conventions, conditions.
    /// Rules define relationships between facts and govern decisions.
    /// </summary>
    Rule,

    /// <summary>
    /// User directives, preferences, standing orders, explicit guidance.
    /// Instructions persist until explicitly revoked or superseded.
    /// </summary>
    Instruction,

    /// <summary>
    /// Patterns noticed, inferences, situational context, unverified assessments.
    /// Observations are candidates — they may promote to facts or get pruned as stale.
    /// </summary>
    Observation,

    /// <summary>
    /// Learned workflow patterns — how the agent accomplishes a class of task. Captures ordered steps,
    /// tool-selection preferences, and branch conditions. Parameters vary per invocation; the
    /// <i>structure</i> is the memory.
    /// <para>
    /// Distinct from <see cref="Instruction"/> (a declarative user directive) and from
    /// <see cref="Observation"/> (a noted pattern without executable structure). The planner
    /// promotes Procedural memories ahead of Observations when the query implies an action.
    /// </para>
    /// <para>
    /// Structured step data lives in <c>MemoryEntry.Metadata["__procedure"]</c> as serialized
    /// <see cref="ProceduralSteps"/> JSON; <c>Content</c> holds a human-readable narrative fallback.
    /// </para>
    /// </summary>
    Procedural
}
