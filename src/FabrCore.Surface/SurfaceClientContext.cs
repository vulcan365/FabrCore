using FabrCore.Core;
using FabrCore.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Surface;

public sealed class SurfaceClientContext : ISurfaceClientContext, IClientGrainObserver
{
    private readonly IClusterClient clusterClient;
    private readonly ILogger<SurfaceClientContext> logger;
    private readonly IClientGrain clientGrain;
    private readonly string handle;
    private readonly string handlePrefix;
    private readonly object eventLock = new();

    private IClientGrainObserver? observerRef;
    private DateTime lastObserverRefresh;
    private volatile bool disposed;
    private EventHandler<AgentMessage>? agentMessageReceived;
    private HashSet<string>? trackedAgentsCache;
    private DateTime trackedAgentsCacheExpiry;

    private SurfaceClientContext(
        IClusterClient clusterClient,
        ILogger<SurfaceClientContext> logger,
        string handle,
        IClientGrain clientGrain)
    {
        this.clusterClient = clusterClient;
        this.logger = logger;
        this.handle = handle;
        handlePrefix = $"{handle}:";
        this.clientGrain = clientGrain;
    }

    public string Handle => handle;

    public bool IsDisposed => disposed;

    public event EventHandler<AgentMessage>? AgentMessageReceived
    {
        add
        {
            lock (eventLock)
            {
                agentMessageReceived += value;
            }
        }
        remove
        {
            lock (eventLock)
            {
                agentMessageReceived -= value;
            }
        }
    }

    internal static SurfaceClientContext CreateUninitialized(
        IClusterClient clusterClient,
        ILogger<SurfaceClientContext> logger,
        string handle,
        IClientGrain clientGrain)
        => new(clusterClient, logger, handle, clientGrain);

    internal void SetObserverReference(IClientGrainObserver observerReference)
    {
        if (observerRef != null)
        {
            throw new InvalidOperationException("Observer reference has already been set.");
        }

        observerRef = observerReference;
        lastObserverRefresh = DateTime.UtcNow;
    }

    public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
    {
        ThrowIfDisposed();

        request.FromHandle ??= handle;
        await RefreshObserverIfNeeded();
        return await clientGrain.SendAndReceiveMessage(request);
    }

    public async Task SendMessage(AgentMessage request)
    {
        ThrowIfDisposed();

        request.FromHandle ??= handle;
        await RefreshObserverIfNeeded();
        await clientGrain.SendMessage(request);
    }

    public async Task SendEvent(EventMessage request, string? streamName = null)
    {
        ThrowIfDisposed();

        request.Source ??= handle;
        await RefreshObserverIfNeeded();
        await clientGrain.SendEvent(request, streamName);
    }

    public async Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
    {
        ThrowIfDisposed();

        if (!string.IsNullOrEmpty(agentConfiguration.Handle))
        {
            agentConfiguration.Handle = NormalizeHandle(agentConfiguration.Handle);
        }

        await RefreshObserverIfNeeded();
        return await clientGrain.CreateAgent(agentConfiguration);
    }

    public async Task<AgentHealthStatus> ResetAgent(string handle)
    {
        ThrowIfDisposed();

        await RefreshObserverIfNeeded();
        return await clientGrain.ResetAgent(handle);
    }

    public async Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
    {
        ThrowIfDisposed();

        await RefreshObserverIfNeeded();
        var agentGrain = clusterClient.GetGrain<IAgentGrain>(NormalizeHandle(handle));
        return await agentGrain.GetHealth(detailLevel);
    }

    public async Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false)
    {
        ThrowIfDisposed();
        return await clientGrain.GetTrackedAgents(activate);
    }

    public async Task<bool> IsAgentTracked(string handle)
    {
        ThrowIfDisposed();

        var agentHandle = StripHandlePrefix(handle);
        if (trackedAgentsCache != null && DateTime.UtcNow < trackedAgentsCacheExpiry)
        {
            return trackedAgentsCache.Contains(agentHandle)
                   || trackedAgentsCache.Contains($"{handlePrefix}{agentHandle}");
        }

        var trackedAgents = await clientGrain.GetTrackedAgents();
        trackedAgentsCache = trackedAgents.Select(a => a.Handle).ToHashSet(StringComparer.Ordinal);
        trackedAgentsCacheExpiry = DateTime.UtcNow.AddSeconds(5);

        return trackedAgentsCache.Contains(agentHandle)
               || trackedAgentsCache.Contains($"{handlePrefix}{agentHandle}");
    }

    public async Task<List<AgentInfo>> GetAccessibleSharedAgents()
    {
        ThrowIfDisposed();
        return await clientGrain.GetAccessibleSharedAgents();
    }

    void IClientGrainObserver.OnMessageReceived(AgentMessage message)
    {
        if (disposed)
        {
            logger.LogDebug("Ignoring message received after Surface context disposal for handle {Handle}.", handle);
            return;
        }

        try
        {
            EventHandler<AgentMessage>? handlers;
            lock (eventLock)
            {
                handlers = agentMessageReceived;
            }

            handlers?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching Surface agent message from {FromHandle}.", message.FromHandle);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            if (observerRef != null)
            {
                await clientGrain.Unsubscribe(observerRef);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error unsubscribing Surface context for handle {Handle}.", handle);
        }

        lock (eventLock)
        {
            agentMessageReceived = null;
        }

        GC.SuppressFinalize(this);
    }

    private async Task RefreshObserverIfNeeded()
    {
        if (disposed || observerRef == null)
        {
            return;
        }

        if ((DateTime.UtcNow - lastObserverRefresh).TotalMinutes < 3)
        {
            return;
        }

        try
        {
            await clientGrain.Subscribe(observerRef);
            lastObserverRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh Surface observer subscription for handle {Handle}.", handle);
        }
    }

    private string NormalizeHandle(string value)
        => HandleUtilities.EnsurePrefix(value, handlePrefix);

    private string StripHandlePrefix(string value)
        => HandleUtilities.StripPrefix(value, handlePrefix);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);
}
