namespace FabrCore.Sdk;

/// <summary>
/// Blueprint <c>Args</c> keys that configure a harness agent without code.
/// </summary>
/// <remarks>
/// Follows the existing underscore-prefixed convention used by <c>_Compaction*</c> and <c>_Projection*</c>:
/// a value that fails to parse silently leaves the default in place rather than failing the agent.
/// </remarks>
public static class HarnessArgs
{
    /// <summary>Csv of principal-local, immutable <c>name@version</c> skill references.</summary>
    public const string Skills = "_HarnessSkills";

    /// <summary>Bool. Enables the <c>mode_*</c> tools and operating-mode instructions. Default true.</summary>
    public const string Mode = "_HarnessMode";

    /// <summary>String. Initial mode for a fresh harness session. Default <c>plan</c>.</summary>
    public const string DefaultMode = "_HarnessDefaultMode";

    /// <summary>Bool. Enables the <c>todos_*</c> tools. Default true.</summary>
    public const string Todo = "_HarnessTodo";

    /// <summary>
    /// Csv of <c>todo</c>, <c>background</c>, <c>marker</c>, <c>judge</c>, or <c>none</c>.
    /// Unset means <c>todo</c>, plus <c>background</c> when background agents are configured.
    /// </summary>
    public const string Loop = "_HarnessLoop";

    /// <summary>Int. Iteration cap for the loop. Default 10.</summary>
    public const string LoopMaxIterations = "_HarnessLoopMaxIterations";

    /// <summary>String. Completion marker required by loop mode <c>marker</c>.</summary>
    public const string LoopMarker = "_HarnessLoopMarker";

    /// <summary>String. Chat client config name for the judge in loop mode <c>judge</c>. Defaults to the agent's own model.</summary>
    public const string LoopJudgeModel = "_HarnessLoopJudgeModel";

    /// <summary>String. Judge instructions for loop mode <c>judge</c>.</summary>
    public const string LoopJudgePrompt = "_HarnessLoopJudgePrompt";

    /// <summary>Csv of FabrCore agent handles the model may delegate to.</summary>
    public const string BackgroundAgents = "_HarnessBackgroundAgents";

    /// <summary>Int. Seconds a single delegation may take before it is abandoned as failed. Default 120.</summary>
    public const string BackgroundTimeoutSeconds = "_HarnessBackgroundTimeoutSeconds";

    /// <summary>Int. Function-invocation iterations allowed within one model request. Default 40.</summary>
    public const string MaxIterationsPerRequest = "_HarnessMaxIterationsPerRequest";

    /// <summary>String. Replaces the harness preamble. An empty value drops it entirely.</summary>
    public const string Instructions = "_HarnessInstructions";

    /// <summary>Bool. Persists the harness session across turns and deactivations. Default true.</summary>
    public const string SessionPersistence = "_HarnessSessionPersistence";
}
