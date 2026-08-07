namespace FabrCore.Sdk;

/// <summary>
/// Selects which loop evaluators drive re-invocation of a <see cref="FabrCoreHarnessAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// When no flag is set the agent is single-shot: no loop decorator is applied and the run ends
/// after one complete agent turn (which may still contain many tool calls).
/// </para>
/// <para>
/// Evaluators are consulted in flag order — todo, background, marker, judge — and the first one
/// that asks to continue wins; the rest are skipped for that iteration. An evaluator declining to
/// continue is not a veto over the others.
/// </para>
/// </remarks>
[Flags]
public enum HarnessLoopMode
{
    /// <summary>No loop. The agent runs once per call.</summary>
    None = 0,

    /// <summary>Keep going while incomplete todo items remain. Requires the todo provider.</summary>
    Todo = 1,

    /// <summary>Keep going while background delegations are still running. Requires background agents.</summary>
    Background = 2,

    /// <summary>
    /// Keep going until the response contains a completion marker. Requires
    /// <see cref="FabrCoreHarnessOptions.LoopCompletionMarker"/>. The match is ordinal and case-sensitive.
    /// </summary>
    Marker = 4,

    /// <summary>
    /// Keep going until a judge model rules the request answered. Requires
    /// <see cref="FabrCoreHarnessOptions.LoopJudgeChatClient"/>. Each evaluation costs an extra LLM call.
    /// </summary>
    Judge = 8
}
