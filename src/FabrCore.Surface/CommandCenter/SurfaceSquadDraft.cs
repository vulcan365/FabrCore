using FabrCore.Surface.Ai.Squads;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceSquadDraft
{
    public SurfaceSquadType SquadType { get; set; } = SurfaceSquadType.Orchestrator;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string OrchestratorModel { get; set; } = "default";

    public string OrchestratorSystemPrompt { get; set; } = string.Empty;

    public string TaskPersonaPrompt { get; set; } = string.Empty;

    public string ClientAgentOverlay { get; set; } = string.Empty;

    public int DelegationTimeoutSeconds { get; set; } = 120;

    public int MaxLoopIterations { get; set; } = 10;

    public bool ForceReconfigure { get; set; }

    public string PendingExistingAgentHandle { get; set; } = string.Empty;

    public string PendingExistingMemberKey { get; set; } = string.Empty;

    public List<SurfaceSquadAgentDraft> Agents { get; } = [new()];

    public SurfaceSquadDefinition Build()
        => new()
        {
            SquadType = SquadType,
            Name = Required(Name, "Squad name"),
            Description = Optional(Description),
            OrchestratorModel = string.IsNullOrWhiteSpace(OrchestratorModel) ? "default" : OrchestratorModel.Trim(),
            OrchestratorSystemPrompt = Optional(OrchestratorSystemPrompt),
            ForceReconfigure = ForceReconfigure,
            TaskOptions = new SurfaceTaskSquadOptions
            {
                WorkerModelName = string.IsNullOrWhiteSpace(OrchestratorModel) ? "default" : OrchestratorModel.Trim(),
                PersonaPrompt = SquadType == SurfaceSquadType.Task
                    ? Optional(OrchestratorSystemPrompt)
                    : Optional(TaskPersonaPrompt),
                ClientAgentOverlay = Optional(ClientAgentOverlay),
                DelegationTimeoutSeconds = DelegationTimeoutSeconds > 0 ? DelegationTimeoutSeconds : 120,
                MaxLoopIterations = MaxLoopIterations > 0 ? MaxLoopIterations : 10
            },
            Agents = Agents
                .Where(agent => !string.IsNullOrWhiteSpace(agent.Name)
                                || !string.IsNullOrWhiteSpace(agent.AgentType)
                                || !string.IsNullOrWhiteSpace(agent.Handle))
                .Select(agent => agent.Build())
                .ToList()
        };

    private static string Required(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static string? Optional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class SurfaceSquadAgentDraft
{
    public string Handle { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string AgentTypeSearch { get; set; } = string.Empty;

    public string Models { get; set; } = "default";

    public string Description { get; set; } = string.Empty;

    public SurfaceSquadMemberRole Role { get; set; } = SurfaceSquadMemberRole.Executor;

    public string SystemPrompt { get; set; } = string.Empty;

    public string PluginAliases { get; set; } = string.Empty;

    public string ToolAliases { get; set; } = string.Empty;

    public string PendingPluginAlias { get; set; } = string.Empty;

    public string PendingToolAlias { get; set; } = string.Empty;

    public SurfaceSquadAgentDefinition Build()
        => new()
        {
            Handle = Optional(Handle),
            Name = Required(Name, "Agent name"),
            AgentType = Required(AgentType, "Agent type"),
            Models = string.IsNullOrWhiteSpace(Models) ? "default" : Models.Trim(),
            Description = Optional(Description),
            Role = Role,
            SystemPrompt = Optional(SystemPrompt),
            Plugins = SurfaceAgentConfigurationDraft.SplitList(PluginAliases),
            Tools = SurfaceAgentConfigurationDraft.SplitList(ToolAliases)
        };

    public IReadOnlyList<string> PluginList
        => SurfaceAgentConfigurationDraft.SplitList(PluginAliases);

    public IReadOnlyList<string> ToolList
        => SurfaceAgentConfigurationDraft.SplitList(ToolAliases);

    public void AddPendingPlugin()
    {
        AddPlugin(PendingPluginAlias);
        PendingPluginAlias = string.Empty;
    }

    public void AddPendingTool()
    {
        AddTool(PendingToolAlias);
        PendingToolAlias = string.Empty;
    }

    public void SelectAgentType(string alias)
    {
        AgentType = alias;
        AgentTypeSearch = alias;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = alias;
        }
    }

    public void AddPlugin(string alias)
        => PluginAliases = AddAlias(PluginAliases, alias);

    public void AddTool(string alias)
        => ToolAliases = AddAlias(ToolAliases, alias);

    public void RemovePlugin(string alias)
        => PluginAliases = RemoveAlias(PluginAliases, alias);

    public void RemoveTool(string alias)
        => ToolAliases = RemoveAlias(ToolAliases, alias);

    private static string AddAlias(string aliases, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return aliases;
        }

        var values = SurfaceAgentConfigurationDraft.SplitList(aliases);
        if (!values.Contains(alias.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            values.Add(alias.Trim());
        }

        return string.Join(Environment.NewLine, values);
    }

    private static string RemoveAlias(string aliases, string alias)
    {
        var values = SurfaceAgentConfigurationDraft.SplitList(aliases);
        values.RemoveAll(value => string.Equals(value, alias, StringComparison.OrdinalIgnoreCase));
        return string.Join(Environment.NewLine, values);
    }

    private static string Required(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static string? Optional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
