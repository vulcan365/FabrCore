namespace FabrCore.Surface.Ai.Swarm;

/// <summary>
/// Maps between the general squad shapes the Surface UI, storage, and blueprints use
/// (<see cref="SurfaceSquadDefinition"/> / <see cref="SurfaceSquad"/> with
/// <see cref="SurfaceSquadType.Swarm"/>) and the Swarm runtime contracts. General squads
/// only persist orchestrator + planner handles; the supervisor and verifier
/// handles are deterministic suffixes of the orchestrator handle.
/// </summary>
public static class SurfaceSwarmInterop
{
    public static bool IsSwarm(SurfaceSquad? squad)
        => squad?.SquadType == SurfaceSquadType.Swarm;

    public static SurfaceSwarmSquadDefinition ToSwarmDefinition(SurfaceSquadDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var options = definition.TaskOptions ?? new SurfaceTaskSquadOptions();
        return new SurfaceSwarmSquadDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            FastModel = BlankToDefault(options.FastModelName),
            OrchestratorModel = BlankToDefault(definition.OrchestratorModel),
            PlannerModel = BlankToDefault(definition.PlannerModel),
            SupervisorModel = BlankToDefault(options.WorkerModelName),
            VerifierModel = BlankToDefault(options.WorkerModelName),
            OrchestratorSystemPrompt = definition.OrchestratorSystemPrompt,
            PlannerSystemPrompt = definition.PlannerSystemPrompt,
            ForceReconfigure = definition.ForceReconfigure,
            Budgets = new SurfaceSwarmBudgets
            {
                MaxTaskAttempts = Positive(options.MaxTaskAttempts, 2),
                MaxValidationAttempts = Positive(options.MaxValidationAttempts, 2),
                PerTaskTimeoutSeconds = Positive(options.DelegationTimeoutSeconds, 180)
            },
            Agents = definition.Agents.Select(ToSwarmAgentDefinition).ToList()
        };
    }

    public static SurfaceSwarmSquadAgentDefinition ToSwarmAgentDefinition(SurfaceSquadAgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new SurfaceSwarmSquadAgentDefinition
        {
            Handle = agent.Handle,
            Name = agent.Name,
            AgentType = agent.AgentType,
            Models = agent.Models,
            Description = agent.Description,
            Role = ToSwarmRole(agent.Role),
            SystemPrompt = agent.SystemPrompt,
            Plugins = [.. agent.Plugins],
            Tools = [.. agent.Tools],
            Streams = [.. agent.Streams],
            Args = new Dictionary<string, string>(agent.Args, StringComparer.OrdinalIgnoreCase),
            McpServers = [.. agent.McpServers]
        };
    }

    public static SurfaceSquad ToSurfaceSquad(SurfaceSwarmSquad squad)
    {
        ArgumentNullException.ThrowIfNull(squad);

        return new SurfaceSquad
        {
            SquadType = SurfaceSquadType.Swarm,
            Name = squad.Name,
            Slug = squad.Slug,
            PrincipalHandle = squad.PrincipalHandle,
            OrchestratorHandle = squad.OrchestratorHandle,
            PlannerHandle = squad.PlannerHandle,
            Description = squad.Description,
            TaskOptions = new SurfaceTaskSquadOptions
            {
                FastModelName = BlankToDefault(squad.FastModel),
                WorkerModelName = BlankToDefault(squad.OrchestratorModel),
                PlannerModelName = BlankToDefault(squad.PlannerModel),
                DelegationTimeoutSeconds = Positive(squad.Budgets.PerTaskTimeoutSeconds, 180),
                MaxTaskAttempts = Positive(squad.Budgets.MaxTaskAttempts, 2),
                MaxValidationAttempts = Positive(squad.Budgets.MaxValidationAttempts, 2)
            },
            Agents = squad.Agents.Select(agent => new SurfaceSquadAgent
            {
                Name = agent.Name,
                Handle = agent.Handle,
                AgentType = agent.AgentType,
                Description = agent.Description,
                Role = ToSurfaceRole(agent.Role)
            }).ToList()
        };
    }

    public static SurfaceSwarmSquad ToSwarmSquad(SurfaceSquad squad)
    {
        ArgumentNullException.ThrowIfNull(squad);

        var options = squad.TaskOptions ?? new SurfaceTaskSquadOptions();
        return new SurfaceSwarmSquad
        {
            Name = squad.Name,
            Slug = squad.Slug,
            PrincipalHandle = squad.PrincipalHandle,
            OrchestratorHandle = squad.OrchestratorHandle,
            PlannerHandle = string.IsNullOrWhiteSpace(squad.PlannerHandle)
                ? $"{squad.OrchestratorHandle}-planner"
                : squad.PlannerHandle,
            SupervisorHandle = SupervisorHandle(squad),
            VerifierHandle = VerifierHandle(squad),
            Description = squad.Description,
            FastModel = BlankToDefault(options.FastModelName),
            OrchestratorModel = BlankToDefault(options.WorkerModelName),
            PlannerModel = BlankToDefault(options.PlannerModelName),
            SupervisorModel = "default",
            VerifierModel = "default",
            Budgets = new SurfaceSwarmBudgets
            {
                MaxTaskAttempts = Positive(options.MaxTaskAttempts, 2),
                MaxValidationAttempts = Positive(options.MaxValidationAttempts, 2),
                PerTaskTimeoutSeconds = Positive(options.DelegationTimeoutSeconds, 180)
            },
            Agents = squad.Agents.Select(agent => new SurfaceSwarmSquadAgent
            {
                Name = agent.Name,
                Handle = agent.Handle,
                AgentType = agent.AgentType,
                Description = agent.Description,
                Role = ToSwarmRole(agent.Role)
            }).ToList()
        };
    }

    public static SurfaceSwarmSquadAgent ToSwarmAgent(SurfaceSquadAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new SurfaceSwarmSquadAgent
        {
            Name = agent.Name,
            Handle = agent.Handle,
            AgentType = agent.AgentType,
            Description = agent.Description,
            Role = ToSwarmRole(agent.Role)
        };
    }

    public static string SupervisorHandle(SurfaceSquad squad)
        => $"{squad.OrchestratorHandle}-supervisor";

    public static string VerifierHandle(SurfaceSquad squad)
        => $"{squad.OrchestratorHandle}-verifier";

    public static SurfaceSwarmSquadMemberRole ToSwarmRole(SurfaceSquadMemberRole role)
        => role switch
        {
            SurfaceSquadMemberRole.SubjectMatterExpert => SurfaceSwarmSquadMemberRole.SubjectMatterExpert,
            SurfaceSquadMemberRole.Helper => SurfaceSwarmSquadMemberRole.Helper,
            _ => SurfaceSwarmSquadMemberRole.Executor
        };

    public static SurfaceSquadMemberRole ToSurfaceRole(SurfaceSwarmSquadMemberRole role)
        => role switch
        {
            SurfaceSwarmSquadMemberRole.SubjectMatterExpert => SurfaceSquadMemberRole.SubjectMatterExpert,
            SurfaceSwarmSquadMemberRole.Helper => SurfaceSquadMemberRole.Helper,
            _ => SurfaceSquadMemberRole.Executor
        };

    private static int Positive(int value, int fallback)
        => value > 0 ? value : fallback;

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
}
