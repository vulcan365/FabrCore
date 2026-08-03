using FabrCore.Core;

namespace FabrCore.Surface.Ai.Squads;

public enum SurfaceSquadType
{
    Orchestrator = 1,
    Task = 2
}

public enum SurfaceSquadMemberRole
{
    Executor = 0,
    SubjectMatterExpert = 1,
    Helper = 2
}

public sealed class SurfaceTaskSquadOptions
{
    /// <summary>Model that backs the task coordinator itself. Squad members use their own configured models.</summary>
    public string WorkerModelName { get; set; } = "default";

    /// <summary>Additional instructions appended to the coordinator's system prompt.</summary>
    public string? PersonaPrompt { get; set; }

    /// <summary>Prepended to every delegation message sent to a squad member.</summary>
    public string? ClientAgentOverlay { get; set; }

    /// <summary>How long a single delegation may run before it is abandoned and reported as failed.</summary>
    public int DelegationTimeoutSeconds { get; set; } = 120;

    /// <summary>Safety cap on coordinator re-invocations within one run.</summary>
    public int MaxLoopIterations { get; set; } = 10;
}

public sealed class SurfaceSquadDefinition
{
    public SurfaceSquadType SquadType { get; set; } = SurfaceSquadType.Orchestrator;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string OrchestratorModel { get; set; } = "default";

    public string? OrchestratorSystemPrompt { get; set; }

    public bool ForceReconfigure { get; set; }

    public SurfaceTaskSquadOptions TaskOptions { get; set; } = new();

    public List<SurfaceSquadAgentDefinition> Agents { get; set; } = [];
}

public sealed class SurfaceSquadAgentDefinition
{
    public string? Handle { get; set; }

    public string Name { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string Models { get; set; } = "default";

    public string? Description { get; set; }

    public SurfaceSquadMemberRole Role { get; set; } = SurfaceSquadMemberRole.Executor;

    public string? SystemPrompt { get; set; }

    public List<string> Plugins { get; set; } = [];

    public List<string> Tools { get; set; } = [];

    public List<string> Streams { get; set; } = [];

    public Dictionary<string, string> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<McpServerConfig> McpServers { get; set; } = [];
}

public sealed class SurfaceSquadAgent
{
    public string Name { get; set; } = string.Empty;

    public string Handle { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SurfaceSquadMemberRole Role { get; set; } = SurfaceSquadMemberRole.Executor;
}

public sealed class SurfaceSquad
{
    public SurfaceSquadType SquadType { get; set; } = SurfaceSquadType.Orchestrator;

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string PrincipalHandle { get; set; } = string.Empty;

    public string OrchestratorHandle { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SurfaceTaskSquadOptions TaskOptions { get; set; } = new();

    public List<SurfaceSquadAgent> Agents { get; set; } = [];
}

public sealed class SurfaceSquadCreateResult
{
    public SurfaceSquad Squad { get; set; } = new();

    public List<AgentHealthStatus> AgentHealth { get; set; } = [];
}
