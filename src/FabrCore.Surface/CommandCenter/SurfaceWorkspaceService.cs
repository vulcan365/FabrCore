using FabrCore.Core;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Squads;
using FabrCore.Surface.Ai.Tasks;
using FabrCore.Surface.Identity;
using FabrCore.Surface.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceWorkspaceService : IAsyncDisposable
{
    private const int MaxTimelineItems = 200;

    private readonly ISurfacePrincipalContextFactory? contextFactory;
    private readonly SurfaceOptions options;
    private readonly ILogger<SurfaceWorkspaceService> logger;
    private readonly IFabrCoreRegistry? registry;
    private readonly ISurfaceDiscoveryClient? discoveryClient;
    private readonly ISurfacePreferencesClient? preferencesClient;
    private readonly ISurfaceSquadConfigClient? squadConfigClient;
    private readonly SurfaceBlueprintProvisioner? blueprintProvisioner;
    private readonly ISurfaceSquadService squadService;
    private readonly SurfaceTranscriptStore transcriptStore;
    private readonly List<SurfaceAgentSummary> agents = [];
    private readonly List<SurfaceAgentSummary> allAgents = [];
    private readonly List<SurfaceSquad> squads = [];
    private readonly List<SurfaceSquad> savedSquads = [];
    private SurfacePreferences preferences;
    private ISurfacePrincipalContext? context;
    private SurfaceDiscoveryResponse? discovery;
    private bool preferencesLoaded;
    private bool squadsLoaded;
    private bool squadAgentsEnsured;
    private int activeSelectedTargetViews;

    public SurfaceWorkspaceService(
        IOptions<SurfaceOptions> options,
        ILogger<SurfaceWorkspaceService> logger,
        ISurfacePrincipalContextFactory? contextFactory = null,
        IServiceProvider? serviceProvider = null,
        ISurfaceDiscoveryClient? discoveryClient = null,
        ISurfaceSquadService? squadService = null,
        ISurfacePreferencesClient? preferencesClient = null,
        ISurfaceSquadConfigClient? squadConfigClient = null,
        SurfaceBlueprintProvisioner? blueprintProvisioner = null)
    {
        this.contextFactory = contextFactory ?? serviceProvider?.GetService<ISurfacePrincipalContextFactory>();
        this.options = options.Value;
        this.logger = logger;
        registry = serviceProvider?.GetService<IFabrCoreRegistry>();
        this.discoveryClient = discoveryClient ?? serviceProvider?.GetService<ISurfaceDiscoveryClient>();
        this.preferencesClient = preferencesClient ?? serviceProvider?.GetService<ISurfacePreferencesClient>();
        this.squadConfigClient = squadConfigClient ?? serviceProvider?.GetService<ISurfaceSquadConfigClient>();
        this.blueprintProvisioner = blueprintProvisioner ?? serviceProvider?.GetService<SurfaceBlueprintProvisioner>();
        this.squadService = squadService
                                    ?? serviceProvider?.GetService<ISurfaceSquadService>()
                                    ?? new SurfaceSquadService();
        this.transcriptStore = serviceProvider?.GetService<SurfaceTranscriptStore>()
                               ?? new SurfaceTranscriptStore();
        this.transcriptStore.Changed += OnTranscriptChanged;
        preferences = SurfacePreferences.FromDefaults(this.options);
    }

    public event Action? Changed;

    public FabrCore.Surface.Identity.SurfacePrincipalContext? Principal { get; private set; }

    public ISurfacePrincipalContext? PrincipalContext => context;

    public IReadOnlyList<SurfaceAgentSummary> Agents => agents;

    public IReadOnlyList<SurfaceAgentSummary> AllAgents => allAgents;

    public IReadOnlyList<SurfaceSquad> Squads => squads;

    public IReadOnlyList<SurfaceTimelineItem> Timeline => transcriptStore.GetTimeline(Principal?.PrincipalId);

    public SurfaceDiscoveryResponse? Discovery => discovery;

    public string? DiscoveryError { get; private set; }

    public SurfaceAgentSummary? SelectedAgent { get; private set; }

    public SurfaceSquad? SelectedSquad { get; private set; }

    public bool ShowHiddenAgents { get; private set; }

    public bool ShowRunningAgents { get; private set; }

    public bool IsInitialized => context is not null && Principal?.IsResolved == true;

    public int TotalUnreadCount => transcriptStore.GetTotalUnreadCount(Principal?.PrincipalId);

    public IDisposable ActivateSelectedTargetView()
    {
        activeSelectedTargetViews++;
        return new ActiveSelectedTargetViewLease(this);
    }

    public async Task InitializeAsync(FabrCore.Surface.Identity.SurfacePrincipalContext principal, CancellationToken cancellationToken = default)
    {
        if (!principal.IsResolved)
        {
            throw new InvalidOperationException("Surface principal context must be resolved before initializing the workspace.");
        }

        if (context is not null)
        {
            if (string.Equals(Principal?.PrincipalId, principal.PrincipalId, StringComparison.OrdinalIgnoreCase))
            {
                Principal = principal;
                await LoadPreferencesAsync(cancellationToken);
                await ApplyStoredBlueprintAsync(cancellationToken);
                await LoadSquadConfigurationsAsync(cancellationToken);
                await EnsureSavedSquadsConfiguredAsync(cancellationToken);
                await RefreshAgentsAsync(cancellationToken);
                return;
            }

            context.AgentMessageReceived -= OnAgentMessageReceived;
            context = null;
            preferencesLoaded = false;
            squadsLoaded = false;
            squadAgentsEnsured = false;
            savedSquads.Clear();
            squads.Clear();
            SelectedSquad = null;
        }

        Principal = principal;
        if (contextFactory is not null)
        {
            context = await contextFactory.GetOrCreateAsync(principal.PrincipalId!, cancellationToken);
            context.AgentMessageReceived += OnAgentMessageReceived;
        }
        else
        {
            // Hosts without a cluster connection (e.g. admin consoles reaching clusters over
            // HTTP) run the workspace without a live agent context; remote clients still load.
            logger.LogDebug("No ISurfacePrincipalContextFactory registered; Surface workspace runs without a live agent context.");
        }

        await LoadPreferencesAsync(cancellationToken);
        await ApplyStoredBlueprintAsync(cancellationToken);
        await LoadSquadConfigurationsAsync(cancellationToken);
        await EnsureSavedSquadsConfiguredAsync(cancellationToken);
        await RefreshAgentsAsync(cancellationToken);
    }

    public async Task<SurfaceDiscoveryResponse?> LoadDiscoveryAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && discovery is not null)
        {
            return discovery;
        }

        if (discoveryClient is null)
        {
            DiscoveryError = "Surface discovery client is not registered.";
            return null;
        }

        try
        {
            DiscoveryError = null;
            discovery = await discoveryClient.GetDiscoveryAsync(cancellationToken);
            Changed?.Invoke();
            return discovery;
        }
        catch (Exception ex)
        {
            DiscoveryError = $"Discovery failed: {ex.Message}";
            logger.LogWarning(ex, "Failed to load Surface discovery metadata.");
            Changed?.Invoke();
            return null;
        }
    }

    public async Task RefreshAgentsAsync(CancellationToken cancellationToken = default)
    {
        if (context is null || Principal?.PrincipalId is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<TrackedAgentInfo> trackedAgents = options.EnableAgentDirectory
            ? await context.GetTrackedAgents(activate: false)
            : [];
        IReadOnlyList<AgentInfo> sharedAgents = options.EnableSharedAgents
            ? await context.GetAccessibleSharedAgents()
            : [];

        var registryHiddenTypes = ResolveRegistryHiddenAgentTypes(trackedAgents, sharedAgents);
        allAgents.Clear();
        allAgents.AddRange(SurfaceAgentList.Merge(
            Principal.PrincipalId,
            trackedAgents,
            sharedAgents,
            options.HiddenAgentTypes.Concat(registryHiddenTypes),
            options.HiddenAgentHandles,
            preferences.SurfaceAgentHandles,
            includeHidden: true));

        agents.Clear();
        agents.AddRange(allAgents
            .Where(ShouldShowInCommandCenter)
            .OrderBy(agent => agent.IsSurfaceAgent ? 0 : 1)
            .ThenBy(agent => agent.IsHidden)
            .ThenBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase));
        ApplyUnreadCounts();

        await RefreshSquadsAsync(trackedAgents, cancellationToken);

        if (SelectedSquad is not null)
        {
            var refreshed = squads.FirstOrDefault(squad =>
                string.Equals(squad.OrchestratorHandle, SelectedSquad.OrchestratorHandle, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
            {
                SelectedSquad = refreshed;
                SelectedAgent = BuildSquadSummary(refreshed);
            }
        }

        if (SelectedSquad is null
            && SelectedAgent is not null
            && allAgents.All(a => !string.Equals(a.Handle, SelectedAgent.Handle, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedAgent = null;
        }

        if (SelectedSquad is null && SelectedAgent is null)
        {
            SelectedSquad = squads.FirstOrDefault();
            SelectedAgent = SelectedSquad is null
                ? agents.FirstOrDefault()
                : BuildSquadSummary(SelectedSquad);
        }
        else if (SelectedSquad is null && SelectedAgent is not null)
        {
            SelectedAgent = allAgents.First(a => string.Equals(a.Handle, SelectedAgent.Handle, StringComparison.OrdinalIgnoreCase));
        }

        Changed?.Invoke();
    }

    public async Task RefreshFromStorageAsync(CancellationToken cancellationToken = default)
    {
        if (context is null || Principal?.PrincipalId is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await ApplyStoredBlueprintAsync(cancellationToken);

        squadsLoaded = false;
        squadAgentsEnsured = false;
        savedSquads.Clear();
        squads.Clear();

        await LoadSquadConfigurationsAsync(cancellationToken);
        await EnsureSavedSquadsConfiguredAsync(cancellationToken);
        await RefreshAgentsAsync(cancellationToken);
    }

    public void SelectAgent(string handle)
    {
        var normalized = NormalizeHandle(handle);
        SelectedSquad = null;
        SelectedAgent = allAgents.FirstOrDefault(a => string.Equals(a.Handle, normalized, StringComparison.OrdinalIgnoreCase))
                        ?? allAgents.FirstOrDefault(a => string.Equals(a.Handle, handle, StringComparison.OrdinalIgnoreCase));
        var clearedUnread = MarkAgentSeenCore(SelectedAgent?.Handle);
        if (clearedUnread)
        {
            NotifyTimelineChanged();
        }

        Changed?.Invoke();
    }

    public void SelectSquad(string orchestratorHandle)
    {
        var normalized = NormalizeHandle(orchestratorHandle);
        SelectedSquad = squads.FirstOrDefault(squad =>
                            string.Equals(squad.OrchestratorHandle, normalized, StringComparison.OrdinalIgnoreCase))
                        ?? squads.FirstOrDefault(squad =>
                            string.Equals(squad.OrchestratorHandle, orchestratorHandle, StringComparison.OrdinalIgnoreCase));
        SelectedAgent = SelectedSquad is null ? null : BuildSquadSummary(SelectedSquad);
        var clearedUnread = MarkAgentSeenCore(SelectedAgent?.Handle);
        if (clearedUnread)
        {
            NotifyTimelineChanged();
        }

        Changed?.Invoke();
    }

    public IEnumerable<SurfaceTimelineItem> GetTimelineForAgent(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return [];
        }

        return Timeline.Where(item => BelongsToAgentTimeline(item, handle));
    }

    public IEnumerable<SurfaceTimelineItem> GetVisibleTimelineForAgent(string? handle)
        => GetTimelineForAgent(handle).Where(item => item.DisplayInChat);

    public void MarkAgentSeen(string? handle)
    {
        if (!MarkAgentSeenCore(handle))
        {
            return;
        }

        NotifyTimelineChanged();
    }

    public IReadOnlyList<SurfaceUnreadSummary> GetUnreadSummaries()
        => transcriptStore.GetUnreadCounts(Principal?.PrincipalId)
            .Where(pair => pair.Value > 0)
            .Select(pair =>
            {
                var agent = FindAgentSummary(pair.Key);
                var squad = squads.FirstOrDefault(squad =>
                    string.Equals(squad.OrchestratorHandle, pair.Key, StringComparison.OrdinalIgnoreCase));
                return new SurfaceUnreadSummary(
                    pair.Key,
                    agent?.DisplayName
                    ?? squad?.Name
                    ?? SurfaceAgentList.ToDisplayName(pair.Key, Principal?.PrincipalId ?? string.Empty),
                    pair.Value,
                    squad is not null);
            })
            .OrderByDescending(summary => summary.UnreadCount)
            .ThenBy(summary => summary.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void MarkAllSeen()
    {
        if (Principal?.PrincipalId is not { Length: > 0 } principalId)
        {
            return;
        }

        var handles = transcriptStore.ClearAllUnread(principalId);
        if (handles.Count == 0)
        {
            return;
        }

        foreach (var handle in handles)
        {
            SetAgentUnreadCount(handle, 0);
        }

        NotifyTimelineChanged();
    }

    public async Task<AgentHealthStatus> CreateAgentAsync(
        AgentConfiguration agentConfiguration,
        CancellationToken cancellationToken = default)
    {
        if (!options.EnableAgentCreate)
        {
            throw new InvalidOperationException("Agent creation is disabled for this Surface instance.");
        }

        if (context is null || Principal?.PrincipalId is null)
        {
            throw new InvalidOperationException("Surface workspace must be initialized before creating an agent.");
        }

        if (string.IsNullOrWhiteSpace(agentConfiguration.Handle))
        {
            throw new InvalidOperationException("Agent handle is required.");
        }

        if (string.IsNullOrWhiteSpace(agentConfiguration.AgentType))
        {
            throw new InvalidOperationException("Agent type is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var health = await context.CreateAgent(agentConfiguration);
        var handleToSelect = agentConfiguration.Handle ?? health.Handle;
        if (!string.IsNullOrWhiteSpace(handleToSelect))
        {
            if (!handleToSelect.Contains(':', StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(Principal.PrincipalId))
            {
                handleToSelect = $"{Principal.PrincipalId}:{handleToSelect}";
            }

            await SetSurfaceAgentAsync(handleToSelect, true, refresh: false, cancellationToken);
        }

        await RefreshAgentsAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(handleToSelect))
        {
            SelectAgent(handleToSelect);
        }

        await RefreshSelectedHealthAsync(cancellationToken);
        return health;
    }

    public async Task<SurfaceSquadCreateResult> CreateSquadAsync(
        SurfaceSquadDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (!options.EnableAgentCreate)
        {
            throw new InvalidOperationException("Squad creation is disabled for this Surface instance.");
        }

        if (context is null || Principal?.PrincipalId is null)
        {
            throw new InvalidOperationException("Surface workspace must be initialized before creating a squad.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await squadService.CreateSquadAsync(context, Principal.PrincipalId, definition, cancellationToken);

        result.Squad.SquadType = definition.SquadType;
        UpsertSquad(result.Squad);
        await SaveSquadAsync(result.Squad, cancellationToken);
        await RefreshAgentsAsync(cancellationToken);
        UpsertSquad(result.Squad);
        SelectSquad(result.Squad.OrchestratorHandle);
        await RefreshSelectedHealthAsync(cancellationToken);
        return result;
    }

    public async Task<SurfaceSquad> AddExistingAgentToSelectedSquadAsync(
        string agentHandle,
        string? agentName = null,
        SurfaceSquadMemberRole role = SurfaceSquadMemberRole.Executor,
        CancellationToken cancellationToken = default)
    {
        if (context is null || Principal?.PrincipalId is null)
        {
            throw new InvalidOperationException("Surface workspace must be initialized before adding squad agents.");
        }

        var squad = ResolveSelectedSquad()
                      ?? throw new InvalidOperationException("Select a squad before adding an agent.");
        var summary = allAgents.FirstOrDefault(agent => string.Equals(agent.Handle, agentHandle, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Agent '{agentHandle}' is not available.");
        var squadAgent = new SurfaceSquadAgent
        {
            Name = string.IsNullOrWhiteSpace(agentName)
                ? SurfaceSquadHandleBuilder.DisplayNameFromHandle(summary.Handle)
                : agentName.Trim(),
            Handle = summary.Handle,
            AgentType = summary.AgentType,
            Role = role,
            Description = summary.Health?.Configuration?.Description
        };

        var updated = await squadService.AddExistingAgentAsync(context, squad, squadAgent, cancellationToken);
        UpsertSquad(updated);
        await SaveSquadAsync(updated, cancellationToken);
        SelectSquad(updated.OrchestratorHandle);
        Changed?.Invoke();
        return updated;
    }

    public async Task<SurfaceSquad> RemoveAgentFromSelectedSquadAsync(
        string agentHandle,
        CancellationToken cancellationToken = default)
    {
        if (context is null || Principal?.PrincipalId is null)
        {
            throw new InvalidOperationException("Surface workspace must be initialized before removing squad agents.");
        }

        var squad = ResolveSelectedSquad()
                      ?? throw new InvalidOperationException("Select a squad before removing an agent.");

        var updated = await squadService.RemoveAgentAsync(context, squad, agentHandle, cancellationToken);
        UpsertSquad(updated);
        await SaveSquadAsync(updated, cancellationToken);
        SelectSquad(updated.OrchestratorHandle);
        Changed?.Invoke();
        return updated;
    }

    public async Task<SurfaceSquadCreateResult> CreateAgentForSelectedSquadAsync(
        SurfaceSquadAgentDefinition agentDefinition,
        CancellationToken cancellationToken = default)
    {
        if (context is null || Principal?.PrincipalId is null)
        {
            throw new InvalidOperationException("Surface workspace must be initialized before creating squad agents.");
        }

        var squad = ResolveSelectedSquad()
                      ?? throw new InvalidOperationException("Select a squad before creating an agent.");
        var result = await squadService.CreateSquadAgentAsync(context, squad, agentDefinition, cancellationToken);

        UpsertSquad(result.Squad);
        await SaveSquadAsync(result.Squad, cancellationToken);
        await RefreshAgentsAsync(cancellationToken);
        UpsertSquad(result.Squad);
        SelectSquad(result.Squad.OrchestratorHandle);
        Changed?.Invoke();
        return result;
    }

    public async Task SetShowHiddenAgentsAsync(bool showHiddenAgents, CancellationToken cancellationToken = default)
    {
        if (ShowHiddenAgents == showHiddenAgents)
        {
            return;
        }

        ShowHiddenAgents = showHiddenAgents;
        preferences.ShowHiddenAgents = showHiddenAgents;
        await SavePreferencesAsync(cancellationToken);
        await RefreshAgentsAsync(cancellationToken);
    }

    public async Task SetShowRunningAgentsAsync(bool showRunningAgents, CancellationToken cancellationToken = default)
    {
        if (ShowRunningAgents == showRunningAgents)
        {
            return;
        }

        ShowRunningAgents = showRunningAgents;
        preferences.ShowRunningAgents = showRunningAgents;
        await SavePreferencesAsync(cancellationToken);
        await RefreshAgentsAsync(cancellationToken);
    }

    public Task SetSurfaceAgentAsync(
        string handle,
        bool isSurfaceAgent,
        CancellationToken cancellationToken = default)
        => SetSurfaceAgentAsync(handle, isSurfaceAgent, refresh: true, cancellationToken);

    public async Task SendChatAsync(string message, CancellationToken cancellationToken = default)
        => await SendChatAsync(message, targetAgentHandle: null, cancellationToken);

    public async Task SendChatAsync(
        string message,
        string? targetAgentHandle,
        CancellationToken cancellationToken = default)
        => await SendChatAsync(message, targetAgentHandle, fileIds: null, cancellationToken);

    public async Task SendChatAsync(
        string message,
        IReadOnlyCollection<string>? fileIds,
        CancellationToken cancellationToken = default)
        => await SendChatAsync(message, targetAgentHandle: null, fileIds, cancellationToken);

    public async Task SendChatAsync(
        string message,
        string? targetAgentHandle,
        IReadOnlyCollection<string>? fileIds,
        CancellationToken cancellationToken = default)
    {
        if (context is null || Principal?.PrincipalId is null || !options.EnableAgentChat)
        {
            return;
        }

        var targetAgent = ResolveChatTarget(targetAgentHandle);
        if (targetAgent is null)
        {
            return;
        }

        var text = message.Trim();
        var attachedFileIds = fileIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (string.IsNullOrWhiteSpace(text) && attachedFileIds.Count == 0)
        {
            return;
        }

        var selectedSquad = string.IsNullOrWhiteSpace(targetAgentHandle)
            ? ResolveSelectedSquad()
            : null;
        var timelineHandle = selectedSquad?.OrchestratorHandle ?? targetAgent.Handle;
        var outboundText = text;
        var targetHandle = targetAgent.Handle;
        var routedMention = string.Empty;

        if (selectedSquad is not null && !string.IsNullOrWhiteSpace(text))
        {
            await EnsureSquadConfiguredAsync(selectedSquad, cancellationToken);

            var route = ResolveSquadRoute(selectedSquad, text);
            if (!route.Success)
            {
                AddTimelineItem(new SurfaceTimelineItem
                {
                    AgentHandle = selectedSquad.OrchestratorHandle,
                    Kind = SurfaceTimelineItemKind.Error,
                    Author = selectedSquad.Name,
                    MessageType = SystemMessageTypes.Error,
                    IsSystemMessage = true,
                    Text = route.Error
                });
                return;
            }

            targetHandle = route.TargetHandle;
            outboundText = route.Message;
            routedMention = route.Mention ?? string.Empty;
        }

        if (!await EnsureChatTargetHealthyAsync(targetHandle, timelineHandle, targetAgent, cancellationToken))
        {
            return;
        }

        var request = new AgentMessage
        {
            FromHandle = Principal.PrincipalId,
            ToHandle = targetHandle,
            Message = outboundText,
            MessageType = "chat",
            Kind = options.CommandCenterChatMessageKind == SurfaceChatMessageKind.OneWay
                ? MessageKind.OneWay
                : MessageKind.Request,
            Files = [.. attachedFileIds]
        };

        if (selectedSquad is not null)
        {
            request.Channel = selectedSquad.OrchestratorHandle;
            request.Args ??= new Dictionary<string, string>();
            request.Args[SurfaceSquadArgs.SquadHandle] = selectedSquad.OrchestratorHandle;
            request.Args[SurfaceSquadArgs.SquadName] = selectedSquad.Name;
            request.Args[SurfaceSquadArgs.SquadSlug] = selectedSquad.Slug;
            if (!string.IsNullOrWhiteSpace(routedMention))
            {
                request.Args[SurfaceSquadArgs.RoutedMention] = routedMention;
            }
        }

        AddTimelineItem(new SurfaceTimelineItem
        {
            AgentHandle = timelineHandle,
            Kind = SurfaceTimelineItemKind.Principal,
            Author = Principal.DisplayName ?? Principal.PrincipalId,
            MessageType = "chat",
            DisplayInChat = !SurfaceMessageClassifier.ShouldHideFromChat(request),
            Text = string.IsNullOrWhiteSpace(text) && attachedFileIds.Count > 0
                ? $"{attachedFileIds.Count} file{(attachedFileIds.Count == 1 ? string.Empty : "s")} attached"
                : text,
            SourceMessage = request
        });

        try
        {
            if (options.CommandCenterChatDeliveryMode == SurfaceChatDeliveryMode.FireAndForget
                && selectedSquad is null)
            {
                logger.LogInformation(
                    "Surface command center sending fire-and-forget chat from {FromHandle} to {ToHandle}.",
                    request.FromHandle,
                    request.ToHandle);

                await context.SendMessage(request);

                return;
            }

            logger.LogInformation(
                "Surface command center sending request-response chat from {FromHandle} to {ToHandle}.",
                request.FromHandle,
                request.ToHandle);
            var response = await context.SendAndReceiveMessage(request);
            var item = SurfaceMessageClassifier.Classify(response);
            if (selectedSquad is not null)
            {
                item.AgentHandle = selectedSquad.OrchestratorHandle;
            }
            else if (string.IsNullOrWhiteSpace(item.AgentHandle))
            {
                item.AgentHandle = targetAgent.Handle;
            }

            UpdateAgentActivity(response, item);
            if (AddTimelineItem(item, notify: false))
            {
                if (item.DisplayInChat)
                {
                    MarkUnread(item);
                }

                NotifyTimelineChanged();
            }
            else
            {
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            var item = new SurfaceTimelineItem
            {
                AgentHandle = targetAgent.Handle,
                Kind = SurfaceTimelineItemKind.Error,
                Author = targetAgent.DisplayName,
                MessageType = SystemMessageTypes.Error,
                IsSystemMessage = true,
                Text = $"Agent send failed: {ex.Message}"
            };

            AddTimelineItem(item, notify: false);
            targetAgent.LastActivityUtc = DateTime.UtcNow;
            targetAgent.StatusText = item.Text;
            NotifyTimelineChanged();
            logger.LogWarning(ex, "Surface command center chat request failed for {Handle}.", targetAgent.Handle);
        }
    }

    private async Task<bool> EnsureChatTargetHealthyAsync(
        string targetHandle,
        string timelineHandle,
        SurfaceAgentSummary targetAgent,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return false;
        }

        try
        {
            var healthTarget = FindAgentSummary(targetHandle) ?? targetAgent;
            var health = await context.GetAgentHealth(targetHandle, HealthDetailLevel.Basic);
            UpdateAgentHealth(targetHandle, health);
            if (health.IsConfigured && health.State == HealthState.Healthy)
            {
                return true;
            }

            AddUnavailableTimelineItem(
                timelineHandle,
                healthTarget,
                BuildUnavailableMessage(healthTarget, health));
            return false;
        }
        catch (Exception ex)
        {
            var healthTarget = FindAgentSummary(targetHandle) ?? targetAgent;
            AddUnavailableTimelineItem(
                timelineHandle,
                healthTarget,
                $"I couldn't reach {healthTarget.DisplayName} for a quick health check. The agent may be starting up or offline, so I didn't send your message. Try again in a moment.");
            logger.LogWarning(ex, "Surface chat health check failed for {Handle}.", targetHandle);
            return false;
        }
    }

    private void UpdateAgentHealth(string handle, AgentHealthStatus health)
    {
        var summaries = allAgents
            .Concat(agents)
            .Where(agent => string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        if (SelectedAgent is not null
            && string.Equals(SelectedAgent.Handle, handle, StringComparison.OrdinalIgnoreCase)
            && !summaries.Contains(SelectedAgent))
        {
            summaries.Add(SelectedAgent);
        }

        foreach (var summary in summaries)
        {
            summary.Health = health;
            summary.LastActivityUtc = DateTime.UtcNow;
            summary.StatusText = health.State == HealthState.Healthy
                ? null
                : health.Message;
        }
    }

    private void AddUnavailableTimelineItem(
        string timelineHandle,
        SurfaceAgentSummary targetAgent,
        string message)
    {
        AddTimelineItem(new SurfaceTimelineItem
        {
            AgentHandle = timelineHandle,
            Kind = SurfaceTimelineItemKind.Error,
            Author = targetAgent.DisplayName,
            MessageType = SystemMessageTypes.Error,
            IsSystemMessage = true,
            Text = message
        });

        targetAgent.LastActivityUtc = DateTime.UtcNow;
        targetAgent.StatusText = message;
    }

    private static string BuildUnavailableMessage(SurfaceAgentSummary targetAgent, AgentHealthStatus health)
    {
        var detail = string.IsNullOrWhiteSpace(health.Message)
            ? string.Empty
            : $" Health check detail: {health.Message}";

        return health.State switch
        {
            HealthState.NotConfigured when !health.IsConfigured =>
                $"{targetAgent.DisplayName} has not been configured yet, so I didn't send your message. Create or configure the agent, then try again.{detail}",
            HealthState.NotConfigured =>
                $"{targetAgent.DisplayName} is not ready to chat yet, so I didn't send your message. Configure the agent or try again once it is available.{detail}",
            HealthState.Degraded =>
                $"{targetAgent.DisplayName} is reporting a degraded health status, so I didn't send your message. Check the agent setup or try again in a moment.{detail}",
            HealthState.Unhealthy =>
                $"{targetAgent.DisplayName} is unavailable right now, so I didn't send your message. Check the agent health or try again later.{detail}",
            _ when !health.IsConfigured =>
                $"{targetAgent.DisplayName} is not configured yet, so I didn't send your message. Create or configure the agent, then try again.{detail}",
            _ =>
                $"{targetAgent.DisplayName} is not ready to chat right now, so I didn't send your message. Try again once the agent is healthy.{detail}"
        };
    }

    public async Task RefreshSelectedHealthAsync(CancellationToken cancellationToken = default)
    {
        if (context is null || SelectedAgent is null || !options.EnableLiveStatus)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            SelectedAgent.Health = await context.GetAgentHealth(SelectedAgent.Handle, HealthDetailLevel.Detailed);
            SelectedAgent.LastActivityUtc = DateTime.UtcNow;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to refresh Surface agent health for {Handle}.", SelectedAgent.Handle);
        }
    }

    private void OnAgentMessageReceived(object? sender, AgentMessage message)
    {
        var item = SurfaceMessageClassifier.Classify(message);
        UpdateAgentActivity(message, item);
        var added = AddTimelineItem(item, notify: false);
        if (added)
        {
            if (item.DisplayInChat)
            {
                MarkUnread(item);
            }

            NotifyTimelineChanged();
            return;
        }

        if (item.DisplayInChat && IsSelectedTargetActivelyViewed(item.AgentHandle))
        {
            MarkAgentSeenCore(item.AgentHandle);
        }

        Changed?.Invoke();
    }

    private void UpdateAgentActivity(AgentMessage message, SurfaceTimelineItem item)
    {
        if (string.IsNullOrWhiteSpace(message.FromHandle))
        {
            return;
        }

        var agent = allAgents.FirstOrDefault(a => string.Equals(a.Handle, message.FromHandle, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            return;
        }

        agent.LastActivityUtc = DateTime.UtcNow;
        agent.IsWorking = item.Kind == SurfaceTimelineItemKind.Status;
        agent.StatusText = item.Kind is SurfaceTimelineItemKind.Status or SurfaceTimelineItemKind.Error
            ? item.Text
            : null;

        var visible = agents.FirstOrDefault(a => string.Equals(a.Handle, message.FromHandle, StringComparison.OrdinalIgnoreCase));
        if (visible is not null && !ReferenceEquals(visible, agent))
        {
            visible.LastActivityUtc = agent.LastActivityUtc;
            visible.IsWorking = agent.IsWorking;
            visible.StatusText = agent.StatusText;
        }
    }

    private void MarkUnread(SurfaceTimelineItem item)
    {
        if (item.Kind == SurfaceTimelineItemKind.Principal || string.IsNullOrWhiteSpace(item.AgentHandle))
        {
            return;
        }

        if (IsSelectedTargetActivelyViewed(item.AgentHandle))
        {
            MarkAgentSeenCore(item.AgentHandle);
            return;
        }

        var count = transcriptStore.IncrementUnread(Principal?.PrincipalId, item.AgentHandle);
        SetAgentUnreadCount(item.AgentHandle, count);
    }

    private SurfaceAgentSummary? ResolveChatTarget(string? targetAgentHandle)
    {
        if (string.IsNullOrWhiteSpace(targetAgentHandle))
        {
            return SelectedAgent;
        }

        var normalized = NormalizeHandle(targetAgentHandle);
        return allAgents.FirstOrDefault(a => string.Equals(a.Handle, normalized, StringComparison.OrdinalIgnoreCase))
               ?? allAgents.FirstOrDefault(a => string.Equals(a.Handle, targetAgentHandle, StringComparison.OrdinalIgnoreCase))
               ?? new SurfaceAgentSummary
               {
                   Handle = normalized,
                   DisplayName = SurfaceAgentList.ToDisplayName(normalized, Principal?.PrincipalId ?? string.Empty)
               };
    }

    private bool MarkAgentSeenCore(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        var normalized = NormalizeHandle(handle);
        var removed = transcriptStore.ClearUnread(Principal?.PrincipalId, normalized);
        SetAgentUnreadCount(normalized, 0);
        return removed;
    }

    private SurfaceAgentSummary? FindAgentSummary(string handle)
        => allAgents.FirstOrDefault(agent => string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase))
           ?? agents.FirstOrDefault(agent => string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase))
           ?? (SelectedAgent is not null && string.Equals(SelectedAgent.Handle, handle, StringComparison.OrdinalIgnoreCase)
               ? SelectedAgent
               : null);

    private void ApplyUnreadCounts()
    {
        var counts = transcriptStore.GetUnreadCounts(Principal?.PrincipalId);
        foreach (var agent in allAgents)
        {
            agent.UnreadCount = counts.GetValueOrDefault(agent.Handle);
        }

        foreach (var agent in agents)
        {
            agent.UnreadCount = counts.GetValueOrDefault(agent.Handle);
        }
    }

    private void SetAgentUnreadCount(string handle, int count)
    {
        foreach (var agent in allAgents.Where(agent => string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase)))
        {
            agent.UnreadCount = count;
        }

        foreach (var agent in agents.Where(agent => string.Equals(agent.Handle, handle, StringComparison.OrdinalIgnoreCase)))
        {
            agent.UnreadCount = count;
        }
    }

    private static bool BelongsToAgentTimeline(SurfaceTimelineItem item, string handle)
        => string.Equals(item.AgentHandle, handle, StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.SourceMessage?.ToHandle, handle, StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.SourceMessage?.FromHandle, handle, StringComparison.OrdinalIgnoreCase);

    private async Task RefreshSquadsAsync(
        IReadOnlyList<TrackedAgentInfo> trackedAgents,
        CancellationToken cancellationToken)
    {
        var previousSquads = squads.ToList();
        squads.Clear();

        foreach (var savedChannel in savedSquads)
        {
            UpsertSquad(savedChannel);
        }

        if (context is null)
        {
            return;
        }

        foreach (var tracked in trackedAgents.Where(agent =>
                     string.Equals(agent.AgentType, SurfaceOrchestrationAgentTypes.SquadOrchestrator, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var squad = TryReadSquadFromConfiguration(tracked.Health?.Configuration)
                          ?? savedSquads.FirstOrDefault(existing =>
                              string.Equals(existing.OrchestratorHandle, tracked.Handle, StringComparison.OrdinalIgnoreCase))
                          ?? previousSquads.FirstOrDefault(existing =>
                              string.Equals(existing.OrchestratorHandle, tracked.Handle, StringComparison.OrdinalIgnoreCase))
                          ?? BuildFallbackSquad(tracked.Handle, tracked.AgentType);
            UpsertSquad(squad);
        }
    }

    private void UpsertSquad(SurfaceSquad squad)
    {
        squads.RemoveAll(existing =>
            string.Equals(existing.OrchestratorHandle, squad.OrchestratorHandle, StringComparison.OrdinalIgnoreCase));
        squads.Add(squad);
    }

    private SurfaceSquad? ResolveSelectedSquad()
    {
        if (SelectedSquad is not null)
        {
            return SelectedSquad;
        }

        if (SelectedAgent is null)
        {
            return null;
        }

        return squads.FirstOrDefault(squad =>
            string.Equals(squad.OrchestratorHandle, SelectedAgent.Handle, StringComparison.OrdinalIgnoreCase));
    }

    private static SurfaceSquadRouteResult ResolveSquadRoute(SurfaceSquad squad, string text)
        => SurfaceSquadRouteParser.Resolve(squad, text);

    private static SurfaceSquad? TryReadSquadFromConfiguration(AgentConfiguration? configuration)
        => SurfaceSquadService.TryReadSquad(configuration);

    private static SurfaceSquad BuildFallbackSquad(string orchestratorHandle, string agentType)
    {
        var displayName = SurfaceSquadHandleBuilder.DisplayNameFromHandle(orchestratorHandle);
        var (principal, alias) = HandleUtilities.ParseHandle(orchestratorHandle);
        return new SurfaceSquad
        {
            SquadType = string.Equals(agentType, SurfaceTaskAgentTypes.TaskRunner, StringComparison.OrdinalIgnoreCase)
                ? SurfaceSquadType.Task
                : SurfaceSquadType.Orchestrator,
            Name = displayName,
            Slug = SurfaceSquadHandleBuilder.ToSlug(alias),
            PrincipalHandle = principal,
            OrchestratorHandle = orchestratorHandle
        };
    }

    private static SurfaceAgentSummary BuildSquadSummary(SurfaceSquad squad)
        => new()
        {
            Handle = squad.OrchestratorHandle,
            DisplayName = squad.Name,
            AgentType = squad.SquadType == SurfaceSquadType.Task
                ? SurfaceTaskAgentTypes.TaskRunner
                : SurfaceOrchestrationAgentTypes.SquadOrchestrator,
            Health = new AgentHealthStatus
            {
                Handle = squad.OrchestratorHandle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true
            }
        };

    private HashSet<string> ResolveRegistryHiddenAgentTypes(
        IEnumerable<TrackedAgentInfo> trackedAgents,
        IEnumerable<AgentInfo> sharedAgents)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (registry is null)
        {
            return hidden;
        }

        var visibleAliases = registry.GetAgentTypes()
            .SelectMany(entry => entry.Aliases)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var agentType in trackedAgents.Select(a => a.AgentType).Concat(sharedAgents.Select(a => a.AgentType)))
        {
            if (string.IsNullOrWhiteSpace(agentType))
            {
                continue;
            }

            var type = registry.FindAgentType(agentType);
            if (type?.GetCustomAttribute<FabrCoreHiddenAttribute>() is not null
                || (type is not null && !visibleAliases.Contains(agentType)))
            {
                hidden.Add(agentType);
            }
        }

        return hidden;
    }

    public async ValueTask DisposeAsync()
    {
        transcriptStore.Changed -= OnTranscriptChanged;
        if (context is not null)
        {
            context.AgentMessageReceived -= OnAgentMessageReceived;
            context = null;
        }

        await Task.CompletedTask;
    }

    private bool ShouldShowInCommandCenter(SurfaceAgentSummary agent)
    {
        if (agent.IsSurfaceAgent)
        {
            return true;
        }

        return ShowRunningAgents && (!agent.IsHidden || ShowHiddenAgents);
    }

    private async Task LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        if (preferencesLoaded || Principal?.PrincipalId is null)
        {
            return;
        }

        try
        {
            preferences = preferencesClient is null
                ? SurfacePreferences.FromDefaults(options)
                : await preferencesClient.GetAsync(Principal.PrincipalId, options, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load Surface preferences for principal {PrincipalId}; using defaults.", Principal.PrincipalId);
            preferences = SurfacePreferences.FromDefaults(options);
        }

        preferences.SurfaceAgentHandles = new HashSet<string>(preferences.SurfaceAgentHandles, StringComparer.OrdinalIgnoreCase);
        ShowHiddenAgents = preferences.ShowHiddenAgents;
        ShowRunningAgents = preferences.ShowRunningAgents;
        preferencesLoaded = true;
    }

    private async Task LoadSquadConfigurationsAsync(CancellationToken cancellationToken)
    {
        if (squadsLoaded || Principal?.PrincipalId is null)
        {
            return;
        }

        savedSquads.Clear();

        try
        {
            if (squadConfigClient is not null)
            {
                savedSquads.AddRange(
                    (await squadConfigClient.GetAsync(Principal.PrincipalId, cancellationToken))
                    .Select(CloneSquad));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load Surface squad configs for principal {PrincipalId}.", Principal.PrincipalId);
        }

        squadsLoaded = true;
    }

    private async Task ApplyStoredBlueprintAsync(CancellationToken cancellationToken)
    {
        if (blueprintProvisioner is null || Principal?.PrincipalId is not { Length: > 0 } principalId)
        {
            return;
        }

        try
        {
            var result = await blueprintProvisioner.ApplyStoredAsync(principalId, cancellationToken);
            if (result is null)
            {
                return;
            }

            if (result.SquadsCreated > 0)
            {
                squadsLoaded = false;
                squadAgentsEnsured = false;
                savedSquads.Clear();
                squads.Clear();
                SelectedSquad = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply stored Surface blueprint for principal {PrincipalId}.", principalId);
        }
    }

    private async Task EnsureSavedSquadsConfiguredAsync(CancellationToken cancellationToken)
    {
        if (squadAgentsEnsured || context is null)
        {
            return;
        }

        foreach (var squad in savedSquads.Select(CloneSquad).ToList())
        {
            await EnsureSquadConfiguredAsync(squad, cancellationToken);
        }

        squadAgentsEnsured = true;
    }

    private async Task EnsureSquadConfiguredAsync(
        SurfaceSquad squad,
        CancellationToken cancellationToken)
    {
        if (context is null || string.IsNullOrWhiteSpace(squad.OrchestratorHandle))
        {
            return;
        }

        var needsConfiguration = false;
        foreach (var handle in GetSquadShellHandles(squad))
        {
            try
            {
                var health = await context.GetAgentHealth(handle, HealthDetailLevel.Basic);
                if (health.State == HealthState.NotConfigured || !health.IsConfigured)
                {
                    needsConfiguration = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to check Surface squad health for {Handle}.", handle);
                needsConfiguration = true;
                break;
            }
        }

        if (!needsConfiguration)
        {
            return;
        }

        try
        {
            await squadService.EnsureSquadConfiguredAsync(context, squad, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to configure Surface squad {Handle}.", squad.OrchestratorHandle);
        }
    }

    private static IEnumerable<string> GetSquadShellHandles(SurfaceSquad squad)
    {
        if (!string.IsNullOrWhiteSpace(squad.OrchestratorHandle))
        {
            yield return squad.OrchestratorHandle;
        }
    }

    private async Task SavePreferencesAsync(CancellationToken cancellationToken)
    {
        if (preferencesClient is null || Principal?.PrincipalId is null)
        {
            return;
        }

        try
        {
            await preferencesClient.SaveAsync(Principal.PrincipalId, preferences, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save Surface preferences for principal {PrincipalId}.", Principal.PrincipalId);
        }
    }

    private async Task SaveSquadAsync(
        SurfaceSquad squad,
        CancellationToken cancellationToken)
    {
        if (Principal?.PrincipalId is not { Length: > 0 } principalId)
        {
            return;
        }

        savedSquads.RemoveAll(existing =>
            string.Equals(existing.OrchestratorHandle, squad.OrchestratorHandle, StringComparison.OrdinalIgnoreCase));
        savedSquads.Add(CloneSquad(squad));

        if (squadConfigClient is null)
        {
            return;
        }

        try
        {
            await squadConfigClient.SaveAsync(principalId, savedSquads.Select(CloneSquad).ToList(), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save Surface squad configs for principal {PrincipalId}.", principalId);
        }
    }

    private async Task SetSurfaceAgentAsync(
        string handle,
        bool isSurfaceAgent,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        var normalized = NormalizeHandle(handle);
        if (isSurfaceAgent)
        {
            preferences.SurfaceAgentHandles.Add(normalized);
        }
        else
        {
            preferences.SurfaceAgentHandles.Remove(normalized);
            if (Principal?.PrincipalId is { Length: > 0 } principalId)
            {
                preferences.SurfaceAgentHandles.Remove(SurfaceAgentList.ToDisplayName(normalized, principalId));
            }
        }

        await SavePreferencesAsync(cancellationToken);
        if (refresh)
        {
            await RefreshAgentsAsync(cancellationToken);
        }
    }

    private string NormalizeHandle(string handle)
    {
        if (handle.Contains(':', StringComparison.Ordinal) || Principal?.PrincipalId is not { Length: > 0 } principalId)
        {
            return handle;
        }

        return $"{principalId}:{handle}";
    }

    private bool IsSelectedTargetActivelyViewed(string? handle)
        => activeSelectedTargetViews > 0
           && !string.IsNullOrWhiteSpace(handle)
           && string.Equals(handle, SelectedAgent?.Handle, StringComparison.OrdinalIgnoreCase);

    private void ReleaseSelectedTargetView()
    {
        if (activeSelectedTargetViews > 0)
        {
            activeSelectedTargetViews--;
        }
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

    private static SurfaceTaskSquadOptions CloneTaskOptions(SurfaceTaskSquadOptions? options)
        => new()
        {
            WorkerModelName = string.IsNullOrWhiteSpace(options?.WorkerModelName) ? "default" : options.WorkerModelName.Trim(),
            PersonaPrompt = string.IsNullOrWhiteSpace(options?.PersonaPrompt) ? null : options.PersonaPrompt.Trim(),
            ClientAgentOverlay = string.IsNullOrWhiteSpace(options?.ClientAgentOverlay) ? null : options.ClientAgentOverlay.Trim(),
            DelegationTimeoutSeconds = options?.DelegationTimeoutSeconds > 0 ? options.DelegationTimeoutSeconds : 120,
            MaxLoopIterations = options?.MaxLoopIterations > 0 ? options.MaxLoopIterations : 10
        };

    private bool AddTimelineItem(SurfaceTimelineItem item, bool notify = true)
    {
        if (Principal?.PrincipalId is not { Length: > 0 } principalId)
        {
            return false;
        }

        var added = transcriptStore.Add(principalId, item, MaxTimelineItems);
        if (added && notify)
        {
            transcriptStore.NotifyChanged(principalId);
        }

        return added;
    }

    private void NotifyTimelineChanged()
    {
        if (Principal?.PrincipalId is { Length: > 0 } principalId)
        {
            transcriptStore.NotifyChanged(principalId);
        }
    }

    private void OnTranscriptChanged(string principalId)
    {
        if (string.Equals(principalId, Principal?.PrincipalId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyUnreadCounts();
            Changed?.Invoke();
        }
    }

    private sealed class ActiveSelectedTargetViewLease : IDisposable
    {
        private SurfaceWorkspaceService? workspace;

        public ActiveSelectedTargetViewLease(SurfaceWorkspaceService workspace)
        {
            this.workspace = workspace;
        }

        public void Dispose()
        {
            workspace?.ReleaseSelectedTargetView();
            workspace = null;
        }
    }
}
