namespace FabrCore.Sdk;

/// <summary>Reserved <see cref="FabrCore.Core.AgentMessage.Args"/> keys understood by the harness run wrapper.</summary>
public static class HarnessMessageArgs
{
    /// <summary>
    /// Bool. Selects the starting operating mode for this run: missing, invalid, or <c>true</c> selects
    /// the configured planning mode; <c>false</c> selects the configured execution mode.
    /// </summary>
    public const string PlanMode = "_plan-mode";
}
