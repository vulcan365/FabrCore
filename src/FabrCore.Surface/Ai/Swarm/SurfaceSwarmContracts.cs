using FabrCore.Core;

namespace FabrCore.Surface.Ai.Swarm;

public enum SurfaceSquadType
{
    Orchestrator = 1,
    Task = 2,
    Swarm = 3
}

public enum SurfaceSquadMemberRole
{
    Executor = 0,
    SubjectMatterExpert = 1,
    Helper = 2
}

public sealed class SurfaceTaskSquadOptions
{
    public string FastModelName { get; set; } = "default";

    public string WorkerModelName { get; set; } = "default";

    public string PlannerModelName { get; set; } = "default";

    public string? PersonaPrompt { get; set; }

    public string? ClientAgentOverlay { get; set; }

    public int DelegationTimeoutSeconds { get; set; } = 120;

    public int MaxTaskAttempts { get; set; } = 2;

    public int MaxValidationAttempts { get; set; } = 2;
}

public sealed class SurfaceSquadDefinition
{
    public SurfaceSquadType SquadType { get; set; } = SurfaceSquadType.Swarm;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string OrchestratorModel { get; set; } = "default";

    public string PlannerModel { get; set; } = "default";

    public string? OrchestratorSystemPrompt { get; set; }

    public string? PlannerSystemPrompt { get; set; }

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
    public SurfaceSquadType SquadType { get; set; } = SurfaceSquadType.Swarm;

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string PrincipalHandle { get; set; } = string.Empty;

    public string OrchestratorHandle { get; set; } = string.Empty;

    public string PlannerHandle { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SurfaceTaskSquadOptions TaskOptions { get; set; } = new();

    public List<SurfaceSquadAgent> Agents { get; set; } = [];
}

public sealed class SurfaceSquadCreateResult
{
    public SurfaceSquad Squad { get; set; } = new();

    public List<AgentHealthStatus> AgentHealth { get; set; } = [];
}
