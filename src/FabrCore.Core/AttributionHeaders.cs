namespace FabrCore.Core;

/// <summary>
/// HTTP header names used to attribute outbound LLM requests to the FabrCore agent that
/// initiated them. Emitted (opt-in) by the SDK's attribution pipeline policy and consumed
/// by OpenAI-compatible gateways (e.g. Forge Gateway) for per-agent metering and limits.
/// Consumers must treat all headers as optional — plain hosts do not send them.
/// </summary>
public static class AttributionHeaders
{
    /// <summary>Full handle of the agent making the LLM call.</summary>
    public const string AgentHandle = "X-FabrCore-Agent-Handle";

    /// <summary>Alias (type) of the agent making the LLM call.</summary>
    public const string AgentAlias = "X-FabrCore-Agent-Alias";

    /// <summary>Trace id correlating the LLM call to the originating user turn.</summary>
    public const string TraceId = "X-FabrCore-Trace-Id";

    /// <summary>Origin context of the call (e.g. OnMessage:&lt;id&gt;, Timer:&lt;name&gt;, Compaction).</summary>
    public const string Origin = "X-FabrCore-Origin";
}
