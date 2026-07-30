using FabrCore.Core;

namespace FabrCore.Surface.Ai.Swarm;

public enum SurfaceSwarmSquadMemberRole
{
    Executor = 0,
    SubjectMatterExpert = 1,
    Helper = 2
}

public sealed class SurfaceSwarmBudgets
{
    public int MaxRounds { get; set; } = 20;

    public int MaxTaskAttempts { get; set; } = 2;

    public int MaxValidationAttempts { get; set; } = 2;

    public int MaxReplans { get; set; } = 2;

    public int MaxConsecutiveStalls { get; set; } = 2;

    public int MaxWallClockMinutes { get; set; } = 30;

    public int PerTaskTimeoutSeconds { get; set; } = 180;

    public int DriveLoopIntervalSeconds { get; set; } = 5;

    public int SmeConsultationTimeoutSeconds { get; set; } = 30;

    public int MaxSmeConsultationsPerPlanningPass { get; set; } = 4;

    public int MaxConcurrencyCeiling { get; set; } = 3;
}

public sealed class SurfaceSwarmOptions
{
    public SurfaceSwarmBudgets DefaultBudgets { get; set; } = new();

    public string DefaultFastModel { get; set; } = "default";

    public string DefaultWorkerModel { get; set; } = "default";
}

public sealed class SurfaceSwarmSquadDefinition
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string FastModel { get; set; } = "default";

    public string OrchestratorModel { get; set; } = "default";

    public string PlannerModel { get; set; } = "default";

    public string SupervisorModel { get; set; } = "default";

    public string VerifierModel { get; set; } = "default";

    public string? OrchestratorSystemPrompt { get; set; }

    public string? PlannerSystemPrompt { get; set; }

    public bool ForceReconfigure { get; set; }

    public SurfaceSwarmBudgets Budgets { get; set; } = new();

    public List<SurfaceSwarmSquadAgentDefinition> Agents { get; set; } = [];
}

public sealed class SurfaceSwarmSquadAgentDefinition
{
    public string? Handle { get; set; }

    public string Name { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string Models { get; set; } = "default";

    public string? Description { get; set; }

    public SurfaceSwarmSquadMemberRole Role { get; set; } = SurfaceSwarmSquadMemberRole.Executor;

    public string? SystemPrompt { get; set; }

    public List<string> Plugins { get; set; } = [];

    public List<string> Tools { get; set; } = [];

    public List<string> Streams { get; set; } = [];

    public Dictionary<string, string> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<McpServerConfig> McpServers { get; set; } = [];
}

public sealed class SurfaceSwarmSquadAgent
{
    public string Name { get; set; } = string.Empty;

    public string Handle { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SurfaceSwarmSquadMemberRole Role { get; set; } = SurfaceSwarmSquadMemberRole.Executor;
}

public sealed class SurfaceSwarmSquad
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string PrincipalHandle { get; set; } = string.Empty;

    public string OrchestratorHandle { get; set; } = string.Empty;

    public string PlannerHandle { get; set; } = string.Empty;

    public string SupervisorHandle { get; set; } = string.Empty;

    public string VerifierHandle { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string FastModel { get; set; } = "default";

    public string OrchestratorModel { get; set; } = "default";

    public string PlannerModel { get; set; } = "default";

    public string SupervisorModel { get; set; } = "default";

    public string VerifierModel { get; set; } = "default";

    public SurfaceSwarmBudgets Budgets { get; set; } = new();

    public List<SurfaceSwarmSquadAgent> Agents { get; set; } = [];
}

public sealed class SurfaceSwarmSquadCreateResult
{
    public SurfaceSwarmSquad Squad { get; set; } = new();

    public List<AgentHealthStatus> AgentHealth { get; set; } = [];
}
