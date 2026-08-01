using FabrCore.Core;

namespace FabrCore.Surface.Ai.Swarm;

public sealed class SurfaceSquadService : ISurfaceSquadService
{
    public async Task<SurfaceSwarmSquadCreateResult> CreateSquadAsync(
        ISurfacePrincipalContext context,
        string principalHandle,
        SurfaceSwarmSquadDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        Validate(definition);

        cancellationToken.ThrowIfCancellationRequested();

        var squad = BuildSquad(principalHandle, definition);

        var runtime = new SurfaceSwarmSquadRuntime { Squad = squad };
        var runtimeJson = SurfaceSwarmSquadRuntime.Serialize(runtime);
        var result = new SurfaceSwarmSquadCreateResult { Squad = squad };

        foreach (var config in BuildAgentConfigurations(definition, squad, runtimeJson))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AgentHealth.Add(await context.CreateAgent(config));
        }

        return result;
    }

    public async Task<SurfaceSwarmSquadCreateResult> EnsureSquadConfiguredAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentException.ThrowIfNullOrWhiteSpace(squad.PrincipalHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(squad.OrchestratorHandle);

        cancellationToken.ThrowIfCancellationRequested();

        var runtimeJson = SurfaceSwarmSquadRuntime.Serialize(new SurfaceSwarmSquadRuntime { Squad = squad });
        var result = new SurfaceSwarmSquadCreateResult { Squad = CloneSquad(squad) };

        foreach (var config in BuildShellConfigurations(squad, runtimeJson, forceReconfigure: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AgentHealth.Add(await context.CreateAgent(config));
        }

        return result;
    }

    public async Task<SurfaceSwarmSquad> AddExistingAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        SurfaceSwarmSquadAgent agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.Handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentType);

        var updated = CloneSquad(squad);
        RemoveAgent(updated, agent.Handle, agent.Name);
        updated.Agents.Add(new SurfaceSwarmSquadAgent
        {
            Name = agent.Name.Trim(),
            Handle = agent.Handle.Trim(),
            AgentType = agent.AgentType.Trim(),
            Role = agent.Role,
            Description = NullIfWhiteSpace(agent.Description)
        });

        await UpdateRuntimeMetadataAsync(context, updated, cancellationToken);
        return updated;
    }

    public async Task<SurfaceSwarmSquad> RemoveAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        string agentHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);

        var updated = CloneSquad(squad);
        var removed = RemoveAgent(updated, agentHandle, agentHandle);
        if (removed > 0)
        {
            await UpdateRuntimeMetadataAsync(context, updated, cancellationToken);
        }

        return updated;
    }

    public async Task<SurfaceSwarmSquadCreateResult> CreateSquadAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        SurfaceSwarmSquadAgentDefinition agentDefinition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(squad);
        ValidateAgent(agentDefinition);

        var updated = CloneSquad(squad);
        var alias = SurfaceSwarmSquadHandleBuilder.BuildMemberAlias(updated.Slug, agentDefinition.Name);
        var handle = SurfaceSwarmSquadHandleBuilder.Qualify(updated.PrincipalHandle, alias);
        RemoveAgent(updated, handle, agentDefinition.Name);
        updated.Agents.Add(new SurfaceSwarmSquadAgent
        {
            Name = agentDefinition.Name.Trim(),
            Handle = handle,
            AgentType = agentDefinition.AgentType.Trim(),
            Role = agentDefinition.Role,
            Description = NullIfWhiteSpace(agentDefinition.Description)
        });

        var runtimeJson = SurfaceSwarmSquadRuntime.Serialize(new SurfaceSwarmSquadRuntime { Squad = updated });
        var config = BuildMemberConfiguration(agentDefinition, updated, runtimeJson, forceReconfigure: true);
        var result = new SurfaceSwarmSquadCreateResult { Squad = updated };
        result.AgentHealth.Add(await context.CreateAgent(config));
        await UpdateRuntimeMetadataAsync(context, updated, cancellationToken);
        return result;
    }

    public static SurfaceSwarmSquad BuildSquad(
        string principalHandle,
        SurfaceSwarmSquadDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        Validate(definition);

        var slug = SurfaceSwarmSquadHandleBuilder.ToSlug(definition.Name);
        return new SurfaceSwarmSquad
        {
            Name = definition.Name.Trim(),
            Slug = slug,
            Description = NullIfWhiteSpace(definition.Description),
            PrincipalHandle = principalHandle,
            OrchestratorHandle = SurfaceSwarmSquadHandleBuilder.Qualify(principalHandle, SurfaceSwarmSquadHandleBuilder.BuildOrchestratorAlias(slug)),
            PlannerHandle = SurfaceSwarmSquadHandleBuilder.Qualify(principalHandle, SurfaceSwarmSquadHandleBuilder.BuildPlannerAlias(slug)),
            SupervisorHandle = SurfaceSwarmSquadHandleBuilder.Qualify(principalHandle, SurfaceSwarmSquadHandleBuilder.BuildSupervisorAlias(slug)),
            VerifierHandle = SurfaceSwarmSquadHandleBuilder.Qualify(principalHandle, SurfaceSwarmSquadHandleBuilder.BuildVerifierAlias(slug)),
            FastModel = BlankToDefault(definition.FastModel),
            OrchestratorModel = BlankToDefault(definition.OrchestratorModel),
            PlannerModel = BlankToDefault(definition.PlannerModel),
            SupervisorModel = BlankToDefault(definition.SupervisorModel),
            VerifierModel = BlankToDefault(definition.VerifierModel),
            Budgets = CloneBudgets(definition.Budgets),
            Agents = definition.Agents.Select(agent => new SurfaceSwarmSquadAgent
            {
                Name = agent.Name.Trim(),
                AgentType = agent.AgentType.Trim(),
                Description = NullIfWhiteSpace(agent.Description),
                Role = agent.Role,
                Handle = ResolveSquadAgentHandle(principalHandle, slug, agent)
            }).ToList()
        };
    }

    public static IEnumerable<AgentConfiguration> BuildAgentConfigurations(
        SurfaceSwarmSquadDefinition definition,
        SurfaceSwarmSquad squad,
        string runtimeJson)
    {
        foreach (var config in BuildShellConfigurations(squad, runtimeJson, definition.ForceReconfigure, definition))
        {
            yield return config;
        }

        foreach (var agent in definition.Agents.Where(agent => string.IsNullOrWhiteSpace(agent.Handle)))
        {
            yield return BuildMemberConfiguration(agent, squad, runtimeJson, definition.ForceReconfigure);
        }
    }

    public static SurfaceSwarmSquad? TryReadSquad(AgentConfiguration? config)
    {
        if (config?.Args is null
            || !config.Args.TryGetValue(SurfaceSwarmArgs.SquadDefinition, out var json)
            || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return SurfaceSwarmSquadRuntime.FromConfiguration(config, config.Handle ?? string.Empty).Squad;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<AgentConfiguration> BuildShellConfigurations(
        SurfaceSwarmSquad squad,
        string runtimeJson,
        bool forceReconfigure,
        SurfaceSwarmSquadDefinition? definition = null)
    {
        yield return new AgentConfiguration
        {
            Handle = squad.PlannerHandle,
            AgentType = SurfaceSwarmAgentTypes.Planner,
            Models = BlankToDefault(squad.PlannerModel),
            Description = $"Planner for Swarm squad {squad.Name}",
            SystemPrompt = BuildPlannerPrompt(definition),
            Args = BuildBaseArgs(squad, runtimeJson, "planner"),
            ForceReconfigure = forceReconfigure
        };

        yield return new AgentConfiguration
        {
            Handle = squad.SupervisorHandle,
            AgentType = SurfaceSwarmAgentTypes.Supervisor,
            Models = BlankToDefault(squad.SupervisorModel),
            Description = $"Supervisor for Swarm squad {squad.Name}",
            Args = BuildBaseArgs(squad, runtimeJson, "supervisor"),
            ForceReconfigure = forceReconfigure
        };

        yield return new AgentConfiguration
        {
            Handle = squad.VerifierHandle,
            AgentType = SurfaceSwarmAgentTypes.Verifier,
            Models = BlankToDefault(squad.VerifierModel),
            Description = $"Verifier for Swarm squad {squad.Name}",
            Args = BuildBaseArgs(squad, runtimeJson, "verifier"),
            ForceReconfigure = forceReconfigure
        };

        yield return new AgentConfiguration
        {
            Handle = squad.OrchestratorHandle,
            AgentType = SurfaceSwarmAgentTypes.Orchestrator,
            Models = BlankToDefault(squad.OrchestratorModel),
            Description = squad.Description ?? $"Orchestrator for Swarm squad {squad.Name}",
            SystemPrompt = BuildOrchestratorPrompt(definition),
            Args = BuildBaseArgs(squad, runtimeJson, "orchestrator"),
            ForceReconfigure = forceReconfigure
        };
    }

    private static AgentConfiguration BuildMemberConfiguration(
        SurfaceSwarmSquadAgentDefinition agent,
        SurfaceSwarmSquad squad,
        string runtimeJson,
        bool forceReconfigure)
    {
        var squadAgent = squad.Agents.First(a =>
            string.Equals(a.Name, agent.Name, StringComparison.OrdinalIgnoreCase));
        var args = BuildBaseArgs(squad, runtimeJson, "member");
        args[SurfaceSwarmArgs.AgentName] = squadAgent.Name;
        args[SurfaceSwarmArgs.AgentRole] = squadAgent.Role.ToString();

        foreach (var (key, value) in agent.Args)
        {
            args[key] = value;
        }

        return new AgentConfiguration
        {
            Handle = squadAgent.Handle,
            AgentType = agent.AgentType.Trim(),
            Models = BlankToDefault(agent.Models),
            Description = NullIfWhiteSpace(agent.Description),
            SystemPrompt = NullIfWhiteSpace(agent.SystemPrompt),
            Plugins = [.. agent.Plugins],
            Tools = [.. agent.Tools],
            Streams = agent.Streams
                .Select(SurfaceEventStreamSubscriptions.Parse)
                .ToList(),
            McpServers = [.. agent.McpServers],
            Args = args,
            ForceReconfigure = forceReconfigure
        };
    }

    private static async Task UpdateRuntimeMetadataAsync(
        ISurfacePrincipalContext context,
        SurfaceSwarmSquad squad,
        CancellationToken cancellationToken)
    {
        var runtimeJson = SurfaceSwarmSquadRuntime.Serialize(new SurfaceSwarmSquadRuntime { Squad = squad });
        var shells = new (string Handle, string AgentType)[]
        {
            (squad.PlannerHandle, SurfaceSwarmAgentTypes.Planner),
            (squad.SupervisorHandle, SurfaceSwarmAgentTypes.Supervisor),
            (squad.VerifierHandle, SurfaceSwarmAgentTypes.Verifier),
            (squad.OrchestratorHandle, SurfaceSwarmAgentTypes.Orchestrator)
        };

        foreach (var (handle, agentType) in shells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var health = await context.GetAgentHealth(handle, HealthDetailLevel.Detailed);
            var config = health.Configuration ?? new AgentConfiguration
            {
                Handle = handle,
                AgentType = agentType,
                Models = "default"
            };

            config.Handle = handle;
            config.Args ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            config.Args[SurfaceSwarmArgs.SquadDefinition] = runtimeJson;
            config.Args[SurfaceSwarmArgs.SquadName] = squad.Name;
            config.Args[SurfaceSwarmArgs.SquadSlug] = squad.Slug;
            config.Args[SurfaceSwarmArgs.SquadHandle] = squad.OrchestratorHandle;
            config.ForceReconfigure = true;
            await context.CreateAgent(config);
        }
    }

    private static string ResolveSquadAgentHandle(
        string principalHandle,
        string squadSlug,
        SurfaceSwarmSquadAgentDefinition agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Handle))
        {
            var handle = agent.Handle.Trim();
            return handle.Contains(':', StringComparison.Ordinal)
                ? handle
                : SurfaceSwarmSquadHandleBuilder.Qualify(principalHandle, handle);
        }

        return SurfaceSwarmSquadHandleBuilder.Qualify(
            principalHandle,
            SurfaceSwarmSquadHandleBuilder.BuildMemberAlias(squadSlug, agent.Name));
    }

    private static Dictionary<string, string> BuildBaseArgs(
        SurfaceSwarmSquad squad,
        string runtimeJson,
        string role)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [SurfaceSwarmArgs.SquadDefinition] = runtimeJson,
            [SurfaceSwarmArgs.SquadName] = squad.Name,
            [SurfaceSwarmArgs.SquadSlug] = squad.Slug,
            [SurfaceSwarmArgs.SquadHandle] = squad.OrchestratorHandle,
            [SurfaceSwarmArgs.AgentRole] = role
        };

    private static void Validate(SurfaceSwarmSquadDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in definition.Agents)
        {
            ValidateAgent(agent);
            if (!names.Add(SurfaceSwarmSquadHandleBuilder.ToSlug(agent.Name)))
            {
                throw new InvalidOperationException($"Duplicate Swarm agent name '{agent.Name}'.");
            }
        }
    }

    private static void ValidateAgent(SurfaceSwarmSquadAgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentType);
    }

    private static SurfaceSwarmSquad CloneSquad(SurfaceSwarmSquad squad)
        => new()
        {
            Name = squad.Name,
            Slug = squad.Slug,
            PrincipalHandle = squad.PrincipalHandle,
            OrchestratorHandle = squad.OrchestratorHandle,
            PlannerHandle = squad.PlannerHandle,
            SupervisorHandle = squad.SupervisorHandle,
            VerifierHandle = squad.VerifierHandle,
            Description = squad.Description,
            FastModel = squad.FastModel,
            OrchestratorModel = squad.OrchestratorModel,
            PlannerModel = squad.PlannerModel,
            SupervisorModel = squad.SupervisorModel,
            VerifierModel = squad.VerifierModel,
            Budgets = CloneBudgets(squad.Budgets),
            Agents = squad.Agents.Select(agent => new SurfaceSwarmSquadAgent
            {
                Name = agent.Name,
                Handle = agent.Handle,
                AgentType = agent.AgentType,
                Role = agent.Role,
                Description = agent.Description
            }).ToList()
        };

    private static SurfaceSwarmBudgets CloneBudgets(SurfaceSwarmBudgets? budgets)
        => new()
        {
            MaxRounds = Positive(budgets?.MaxRounds, 20),
            MaxTaskAttempts = Positive(budgets?.MaxTaskAttempts, 2),
            MaxValidationAttempts = Positive(budgets?.MaxValidationAttempts, 2),
            MaxReplans = budgets?.MaxReplans >= 0 ? budgets.MaxReplans : 2,
            MaxConsecutiveStalls = Positive(budgets?.MaxConsecutiveStalls, 2),
            MaxWallClockMinutes = Positive(budgets?.MaxWallClockMinutes, 30),
            PerTaskTimeoutSeconds = Positive(budgets?.PerTaskTimeoutSeconds, 180),
            DriveLoopIntervalSeconds = Positive(budgets?.DriveLoopIntervalSeconds, 5),
            SmeConsultationTimeoutSeconds = Positive(budgets?.SmeConsultationTimeoutSeconds, 30),
            MaxSmeConsultationsPerPlanningPass = Positive(budgets?.MaxSmeConsultationsPerPlanningPass, 4),
            MaxConcurrencyCeiling = Positive(budgets?.MaxConcurrencyCeiling, 3)
        };

    private static int RemoveAgent(SurfaceSwarmSquad squad, string handle, string name)
        => squad.Agents.RemoveAll(agent =>
            string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string BuildPlannerPrompt(SurfaceSwarmSquadDefinition? definition)
        => string.IsNullOrWhiteSpace(definition?.PlannerSystemPrompt)
            ? "You are the planner for a FabrCore Swarm squad. Decompose goals into a task ledger with dependencies, explicit acceptance criteria, and executor assignments. Consult subject matter experts before finalizing when the request is ambiguous."
            : definition.PlannerSystemPrompt.Trim();

    private static string BuildOrchestratorPrompt(SurfaceSwarmSquadDefinition? definition)
        => string.IsNullOrWhiteSpace(definition?.OrchestratorSystemPrompt)
            ? "You are the orchestrator for a FabrCore Swarm squad. Preserve user intent, keep responses concise, and synthesize squad results into readable answers."
            : definition.OrchestratorSystemPrompt.Trim();

    private static int Positive(int? value, int fallback)
        => value > 0 ? value.Value : fallback;

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
