using FabrCore.Core;
using FabrCore.Surface;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Tasks;

namespace FabrCore.Surface.Ai.Squads;

public sealed class SurfaceSquadService : ISurfaceSquadService
{
    public async Task<SurfaceSquadCreateResult> CreateSquadAsync(
        ISurfacePrincipalContext context,
        string principalHandle,
        SurfaceSquadDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        Validate(definition);

        cancellationToken.ThrowIfCancellationRequested();

        var squad = BuildSquad(principalHandle, definition);

        var runtime = new SurfaceSquadRuntime { Squad = squad };
        var runtimeJson = SurfaceSquadRuntime.Serialize(runtime);
        var result = new SurfaceSquadCreateResult { Squad = squad };

        foreach (var config in BuildAgentConfigurations(definition, squad, runtimeJson))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AgentHealth.Add(await context.CreateAgent(config));
        }

        return result;
    }

    public async Task<SurfaceSquadCreateResult> EnsureSquadConfiguredAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentException.ThrowIfNullOrWhiteSpace(squad.PrincipalHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(squad.OrchestratorHandle);

        cancellationToken.ThrowIfCancellationRequested();

        var runtimeJson = SurfaceSquadRuntime.Serialize(new SurfaceSquadRuntime { Squad = squad });
        var result = new SurfaceSquadCreateResult { Squad = CloneSquad(squad) };

        foreach (var config in BuildSquadShellConfigurations(squad, runtimeJson, forceReconfigure: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AgentHealth.Add(await context.CreateAgent(config));
        }

        return result;
    }

    public static IEnumerable<AgentConfiguration> BuildAgentConfigurations(
        SurfaceSquadDefinition definition,
        SurfaceSquad squad,
        string runtimeJson)
    {
        yield return new AgentConfiguration
        {
            Handle = squad.OrchestratorHandle,
            AgentType = definition.SquadType == SurfaceSquadType.Task
                ? SurfaceTaskAgentTypes.TaskRunner
                : SurfaceOrchestrationAgentTypes.SquadOrchestrator,
            Models = definition.SquadType == SurfaceSquadType.Task
                ? BlankToDefault(squad.TaskOptions.WorkerModelName)
                : BlankToDefault(definition.OrchestratorModel),
            Description = definition.Description ?? (definition.SquadType == SurfaceSquadType.Task
                ? $"Task runner for Surface squad {squad.Name}"
                : $"Orchestrator for squad {squad.Name}"),
            SystemPrompt = BuildOrchestratorPrompt(definition),
            Args = BuildBaseArgs(squad, runtimeJson, "orchestrator"),
            ForceReconfigure = definition.ForceReconfigure
        };

        foreach (var agent in definition.Agents.Where(agent => string.IsNullOrWhiteSpace(agent.Handle)))
        {
            yield return BuildMemberConfiguration(agent, squad, runtimeJson, definition.ForceReconfigure);
        }
    }

    public static SurfaceSquad BuildSquad(
        string principalHandle,
        SurfaceSquadDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        Validate(definition);

        var slug = SurfaceSquadHandleBuilder.ToSlug(definition.Name);
        var orchestratorAlias = SurfaceSquadHandleBuilder.BuildOrchestratorAlias(slug);
        return new SurfaceSquad
        {
            SquadType = definition.SquadType,
            Name = definition.Name.Trim(),
            Slug = slug,
            Description = NullIfWhiteSpace(definition.Description),
            PrincipalHandle = principalHandle,
            OrchestratorHandle = SurfaceSquadHandleBuilder.Qualify(principalHandle, orchestratorAlias),
            TaskOptions = CloneTaskOptions(definition.TaskOptions),
            Agents = definition.Agents.Select(agent => new SurfaceSquadAgent
            {
                Name = agent.Name.Trim(),
                AgentType = agent.AgentType.Trim(),
                Description = NullIfWhiteSpace(agent.Description),
                Role = agent.Role,
                Handle = ResolveSquadAgentHandle(principalHandle, slug, agent)
            }).ToList()
        };
    }

    private static string ResolveSquadAgentHandle(
        string principalHandle,
        string squadSlug,
        SurfaceSquadAgentDefinition agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Handle))
        {
            var handle = agent.Handle.Trim();
            return handle.Contains(':', StringComparison.Ordinal)
                ? handle
                : SurfaceSquadHandleBuilder.Qualify(principalHandle, handle);
        }

        return SurfaceSquadHandleBuilder.Qualify(
            principalHandle,
            SurfaceSquadHandleBuilder.BuildMemberAlias(squadSlug, agent.Name));
    }

    private static IEnumerable<AgentConfiguration> BuildSquadShellConfigurations(
        SurfaceSquad squad,
        string runtimeJson,
        bool forceReconfigure)
    {
        yield return new AgentConfiguration
        {
            Handle = squad.OrchestratorHandle,
            AgentType = squad.SquadType == SurfaceSquadType.Task
                ? SurfaceTaskAgentTypes.TaskRunner
                : SurfaceOrchestrationAgentTypes.SquadOrchestrator,
            Models = squad.SquadType == SurfaceSquadType.Task
                ? BlankToDefault(squad.TaskOptions.WorkerModelName)
                : "default",
            Description = squad.Description ?? (squad.SquadType == SurfaceSquadType.Task
                ? $"Task runner for Surface squad {squad.Name}"
                : $"Orchestrator for squad {squad.Name}"),
            SystemPrompt = squad.SquadType == SurfaceSquadType.Task
                ? NullIfWhiteSpace(squad.TaskOptions.PersonaPrompt)
                : "You are the orchestrator for a FabrCore Surface squad. Use the available squad agents when they can help, synthesize their replies, and keep the squad transcript readable.",
            Args = BuildBaseArgs(squad, runtimeJson, "orchestrator"),
            ForceReconfigure = forceReconfigure
        };
    }

    public async Task<SurfaceSquad> AddExistingAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
        SurfaceSquadAgent agent,
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
        updated.Agents.Add(new SurfaceSquadAgent
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

    public async Task<SurfaceSquad> RemoveAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
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

    public async Task<SurfaceSquadCreateResult> CreateSquadAgentAsync(
        ISurfacePrincipalContext context,
        SurfaceSquad squad,
        SurfaceSquadAgentDefinition agentDefinition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(squad);
        ValidateAgent(agentDefinition);

        var updated = CloneSquad(squad);
        var alias = SurfaceSquadHandleBuilder.BuildMemberAlias(updated.Slug, agentDefinition.Name);
        var handle = SurfaceSquadHandleBuilder.Qualify(updated.PrincipalHandle, alias);
        RemoveAgent(updated, handle, agentDefinition.Name);
        updated.Agents.Add(new SurfaceSquadAgent
        {
            Name = agentDefinition.Name.Trim(),
            Handle = handle,
            AgentType = agentDefinition.AgentType.Trim(),
            Role = agentDefinition.Role,
            Description = NullIfWhiteSpace(agentDefinition.Description)
        });

        var runtimeJson = SurfaceSquadRuntime.Serialize(new SurfaceSquadRuntime { Squad = updated });
        var config = BuildMemberConfiguration(agentDefinition, updated, runtimeJson, forceReconfigure: true);
        var result = new SurfaceSquadCreateResult { Squad = updated };
        result.AgentHealth.Add(await context.CreateAgent(config));
        await UpdateRuntimeMetadataAsync(context, updated, cancellationToken);
        return result;
    }

    public static SurfaceSquad? TryReadSquad(AgentConfiguration? config)
    {
        if (config?.Args is null
            || !config.Args.TryGetValue(SurfaceSquadArgs.SquadDefinition, out var json)
            || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var squad = SurfaceSquadRuntime.FromConfiguration(config, config.Handle ?? string.Empty).Squad;
            if (string.Equals(config.AgentType, SurfaceOrchestrationAgentTypes.SquadOrchestrator, StringComparison.OrdinalIgnoreCase))
            {
                squad.SquadType = SurfaceSquadType.Orchestrator;
            }
            else if (string.Equals(config.AgentType, SurfaceTaskAgentTypes.TaskRunner, StringComparison.OrdinalIgnoreCase))
            {
                squad.SquadType = SurfaceSquadType.Task;
            }

            return squad;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> BuildBaseArgs(
        SurfaceSquad squad,
        string runtimeJson,
        string role)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [SurfaceSquadArgs.SquadDefinition] = runtimeJson,
            [SurfaceSquadArgs.SquadName] = squad.Name,
            [SurfaceSquadArgs.SquadSlug] = squad.Slug,
            [SurfaceSquadArgs.SquadHandle] = squad.OrchestratorHandle,
            [SurfaceSquadArgs.AgentRole] = role
        };

    private static void Validate(SurfaceSquadDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in definition.Agents)
        {
            ValidateAgent(agent);
            if (!names.Add(SurfaceSquadHandleBuilder.ToSlug(agent.Name)))
            {
                throw new InvalidOperationException($"Duplicate squad agent name '{agent.Name}'.");
            }
        }
    }

    private static void ValidateAgent(SurfaceSquadAgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentType);
    }

    private static AgentConfiguration BuildMemberConfiguration(
        SurfaceSquadAgentDefinition agent,
        SurfaceSquad squad,
        string runtimeJson,
        bool forceReconfigure)
    {
        var squadAgent = squad.Agents.First(a =>
            string.Equals(a.Name, agent.Name, StringComparison.OrdinalIgnoreCase));
        var args = BuildBaseArgs(squad, runtimeJson, "member");
        args[SurfaceSquadArgs.AgentName] = squadAgent.Name;
        args[SurfaceSquadArgs.AgentRole] = squadAgent.Role.ToString();

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
        SurfaceSquad squad,
        CancellationToken cancellationToken)
    {
        var runtimeJson = SurfaceSquadRuntime.Serialize(new SurfaceSquadRuntime { Squad = squad });
        cancellationToken.ThrowIfCancellationRequested();
        var health = await context.GetAgentHealth(squad.OrchestratorHandle, HealthDetailLevel.Detailed);
        var config = health.Configuration ?? new AgentConfiguration
        {
            Handle = squad.OrchestratorHandle,
            AgentType = squad.SquadType == SurfaceSquadType.Task
                ? SurfaceTaskAgentTypes.TaskRunner
                : SurfaceOrchestrationAgentTypes.SquadOrchestrator,
            Models = "default"
        };

        config.Handle = squad.OrchestratorHandle;
        config.Args ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.Args[SurfaceSquadArgs.SquadDefinition] = runtimeJson;
        config.Args[SurfaceSquadArgs.SquadName] = squad.Name;
        config.Args[SurfaceSquadArgs.SquadSlug] = squad.Slug;
        config.Args[SurfaceSquadArgs.SquadHandle] = squad.OrchestratorHandle;
        config.ForceReconfigure = true;
        await context.CreateAgent(config);
    }

    private static SurfaceSquad CloneSquad(SurfaceSquad squad)
        => new()
        {
            SquadType = squad.SquadType,
            Name = squad.Name,
            Slug = squad.Slug,
            PrincipalHandle = squad.PrincipalHandle,
            OrchestratorHandle = squad.OrchestratorHandle,
            Description = squad.Description,
            TaskOptions = CloneTaskOptions(squad.TaskOptions),
            Agents = squad.Agents.Select(agent => new SurfaceSquadAgent
            {
                Name = agent.Name,
                Handle = agent.Handle,
                AgentType = agent.AgentType,
                Role = agent.Role,
                Description = agent.Description
            }).ToList()
        };

    private static int RemoveAgent(SurfaceSquad squad, string handle, string name)
        => squad.Agents.RemoveAll(agent =>
            string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string BuildOrchestratorPrompt(SurfaceSquadDefinition definition)
        => string.IsNullOrWhiteSpace(definition.OrchestratorSystemPrompt)
            ? "You are the orchestrator for a FabrCore Surface squad. Use the available squad agents when they can help, synthesize their replies, and keep the squad transcript readable."
            : definition.OrchestratorSystemPrompt.Trim();

    private static string BlankToDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SurfaceTaskSquadOptions CloneTaskOptions(SurfaceTaskSquadOptions? options)
        => new()
        {
            FastModelName = BlankToDefault(options?.FastModelName),
            WorkerModelName = BlankToDefault(options?.WorkerModelName),
            PlannerModelName = BlankToDefault(options?.PlannerModelName),
            PersonaPrompt = NullIfWhiteSpace(options?.PersonaPrompt),
            ClientAgentOverlay = NullIfWhiteSpace(options?.ClientAgentOverlay),
            DelegationTimeoutSeconds = options?.DelegationTimeoutSeconds > 0
                ? options.DelegationTimeoutSeconds
                : 120,
            MaxTaskAttempts = options?.MaxTaskAttempts > 0 ? options.MaxTaskAttempts : 2,
            MaxValidationAttempts = options?.MaxValidationAttempts > 0 ? options.MaxValidationAttempts : 2
        };
}
