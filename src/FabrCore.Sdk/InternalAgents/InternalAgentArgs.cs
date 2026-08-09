namespace FabrCore.Sdk;

/// <summary>Blueprint argument keys that place host-level bounds on private internal specialists.</summary>
public static class InternalAgentArgs
{
    /// <summary>
    /// Maximum number of internal-agent runs allowed concurrently within one proxy activation.
    /// Defaults to 4 and is capped at 32.
    /// </summary>
    public const string MaxConcurrency = "_InternalAgentsMaxConcurrency";
}
