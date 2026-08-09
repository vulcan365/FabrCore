using FabrCore.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk;

/// <summary>Controls where and how a private in-process specialist may execute.</summary>
public enum InternalAgentExecutionPolicy
{
    /// <summary>Allows bounded concurrent runs. Every tool must be classified as read or compute.</summary>
    ConcurrentReadOnly,

    /// <summary>Allows one bounded run at a time. Every tool must be classified as read or compute.</summary>
    SerializedReadOnly,

    /// <summary>The agent may be called directly by the proxy but must not be supplied as a Harness background agent.</summary>
    OrchestratorOnly
}

/// <summary>Security classification applied to an internal specialist's effective tools.</summary>
public enum InternalAgentToolRisk
{
    /// <summary>No classification was supplied. Rejected by the production-safe defaults.</summary>
    Unclassified,

    /// <summary>Reads data without creating a durable external effect.</summary>
    Read,

    /// <summary>Performs local or pure analysis without creating a durable external effect.</summary>
    Compute,

    /// <summary>Creates an external effect and requires a durable, principal-bound approval round trip.</summary>
    ApprovalRequired,

    /// <summary>Administrative or platform capability that is never exposed by default.</summary>
    SystemOnly
}

/// <summary>Options for a private <see cref="AIAgent"/> owned by one <see cref="FabrCoreAgentProxy"/> activation.</summary>
public sealed record InternalAgentOptions
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Instructions { get; init; }
    public required string Model { get; init; }

    /// <summary>A fail-closed scope returned by <c>ResolveInternalAgentToolsAsync</c>.</summary>
    public InternalAgentToolScope? ToolScope { get; init; }

    /// <summary>Direct tools. Prefer <see cref="ToolScope"/> when resolving FabrCore plugins, tools, or MCP servers.</summary>
    public IList<AITool>? Tools { get; init; }

    /// <summary>Risk classification for direct tools, keyed by effective function name.</summary>
    public IReadOnlyDictionary<string, InternalAgentToolRisk>? ToolRisks { get; init; }

    public InternalAgentExecutionPolicy ExecutionPolicy { get; init; } = InternalAgentExecutionPolicy.ConcurrentReadOnly;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Maximum concurrent runs for this specialist. Serialized policies always use one.</summary>
    public int MaxConcurrency { get; init; } = 2;

    public bool EnableContextCompaction { get; init; } = true;
    public bool EnableOpenTelemetry { get; init; } = true;
    public bool EnableSensitiveTelemetryData { get; init; } = true;
}

/// <summary>Options for resolving a distinct, production-safe tool set for one internal specialist.</summary>
public sealed record InternalAgentToolScopeOptions
{
    public required string ScopeName { get; init; }
    public IReadOnlyList<string> Plugins { get; init; } = [];
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>Risk classification keyed by the effective <see cref="AIFunction.Name"/>.</summary>
    public IReadOnlyDictionary<string, InternalAgentToolRisk> ToolRisks { get; init; }
        = new Dictionary<string, InternalAgentToolRisk>(StringComparer.OrdinalIgnoreCase);

    public InternalAgentExecutionPolicy ExecutionPolicy { get; init; } = InternalAgentExecutionPolicy.ConcurrentReadOnly;

    /// <summary>When true (the default), every effective tool must have an explicit risk classification.</summary>
    public bool RequireExplicitRiskClassification { get; init; } = true;
}

/// <summary>A validated capability set with activation-scoped resource ownership.</summary>
public sealed class InternalAgentToolScope : IAsyncDisposable
{
    private readonly IReadOnlyList<IAsyncDisposable> resources;
    private int disposed;

    internal InternalAgentToolScope(
        string name,
        IReadOnlyList<AITool> tools,
        IReadOnlyDictionary<string, InternalAgentToolRisk> toolRisks,
        InternalAgentExecutionPolicy executionPolicy,
        IReadOnlyList<IAsyncDisposable> resources)
    {
        Name = name;
        Tools = tools;
        ToolRisks = toolRisks;
        ExecutionPolicy = executionPolicy;
        this.resources = resources;
    }

    public string Name { get; }
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyDictionary<string, InternalAgentToolRisk> ToolRisks { get; }
    public InternalAgentExecutionPolicy ExecutionPolicy { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var resource in resources.Reverse())
        {
            await resource.DisposeAsync();
        }
    }
}

/// <summary>The bounded agent plus the policy metadata needed by an orchestrator.</summary>
public sealed record InternalAgentResult(
    AIAgent Agent,
    string Name,
    InternalAgentExecutionPolicy ExecutionPolicy,
    TimeSpan Timeout)
{
    /// <summary>
    /// Returns the agent for <see cref="FabrCoreHarnessOptions.BackgroundAgents"/>.
    /// Throws when the policy reserves the agent for direct orchestration.
    /// </summary>
    public AIAgent AsBackgroundAgent() => ExecutionPolicy == InternalAgentExecutionPolicy.OrchestratorOnly
        ? throw new InvalidOperationException($"Internal agent '{Name}' is OrchestratorOnly and cannot run as a Harness background agent.")
        : Agent;
}
