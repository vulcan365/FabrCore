using FabrCore.Core;
using FabrCore.Core.Interfaces;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Orleans;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FabrCore.Client
{
    /// <summary>
    /// Thread-safe client context for communicating with the FabrCore agent cluster.
    /// Each context is immutably bound to a specific handle (user/client identifier).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thread Safety: This class is thread-safe. All operations can be called concurrently
    /// from multiple threads after initialization is complete.
    /// </para>
    /// <para>
    /// Lifecycle: Use IClientContextFactory to create instances. The factory handles
    /// initialization and can optionally manage context caching per handle.
    /// </para>
    /// </remarks>
    public sealed class ClientContext : IClientContext, IClientGrainObserver
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Client.ClientContext");
        private static readonly Meter Meter = new("FabrCore.Client.ClientContext");

        // Metrics
        private static readonly Counter<long> ClientsConnectedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.context.connected",
            description: "Number of client contexts connected");

        private static readonly Counter<long> ClientsDisconnectedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.context.disconnected",
            description: "Number of client contexts disconnected");

        private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.context.messages.processed",
            description: "Number of messages processed by client context");

        private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "fabrcore.client.context.message.duration",
            unit: "ms",
            description: "Duration of client context message processing");

        private static readonly Counter<long> MessagesReceivedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.context.messages.received",
            description: "Number of messages received by observer");

        private static readonly Counter<long> AgentsCreatedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.context.agents.created",
            description: "Number of agents created by client context");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.client.context.errors",
            description: "Number of errors encountered in client context");

        private readonly IClusterClient _clusterClient;
        private readonly ILogger<ClientContext> _logger;
        private readonly IClientGrain _clientGrain;
        private readonly string _handle;
        private readonly string _handlePrefix;
        private readonly object _eventLock = new();

        private IClientGrainObserver? _observerRef;
        private DateTime _lastObserverRefresh;
        private volatile bool _disposed;
        private EventHandler<AgentMessage>? _agentMessageReceived;

        // Local cache for tracked agents (for bulk checks like lazy-loading ChatDocks)
        private HashSet<string>? _trackedAgentsCache;
        private DateTime _trackedAgentsCacheExpiry;

        /// <inheritdoc/>
        public string Handle => _handle;

        /// <inheritdoc/>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// Event raised when an asynchronous message is received from an agent.
        /// Thread-safe add/remove operations.
        /// </summary>
        public event EventHandler<AgentMessage>? AgentMessageReceived
        {
            add
            {
                lock (_eventLock)
                {
                    _agentMessageReceived += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _agentMessageReceived -= value;
                }
            }
        }

        /// <summary>
        /// Private constructor for two-phase initialization.
        /// Use CreateUninitialized + SetObserverReference for proper initialization.
        /// </summary>
        private ClientContext(
            IClusterClient clusterClient,
            ILogger<ClientContext> logger,
            string handle,
            IClientGrain clientGrain)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _handlePrefix = $"{handle}:";
            _clientGrain = clientGrain ?? throw new ArgumentNullException(nameof(clientGrain));
        }

        /// <summary>
        /// Creates an uninitialized context. Must call SetObserverReference before use.
        /// </summary>
        internal static ClientContext CreateUninitialized(
            IClusterClient clusterClient,
            ILogger<ClientContext> logger,
            string handle,
            IClientGrain clientGrain)
        {
            return new ClientContext(clusterClient, logger, handle, clientGrain);
        }

        /// <summary>
        /// Sets the observer reference. Must be called exactly once after CreateUninitialized.
        /// </summary>
        internal void SetObserverReference(IClientGrainObserver observerRef)
        {
            if (_observerRef != null)
            {
                throw new InvalidOperationException("Observer reference has already been set.");
            }

            _observerRef = observerRef ?? throw new ArgumentNullException(nameof(observerRef));
            _lastObserverRefresh = DateTime.UtcNow;
            _logger.LogDebug("ClientContext initialized for handle: {Handle}", _handle);
        }

        /// <inheritdoc/>
        public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            ThrowIfDisposed();

            using var activity = ActivitySource.StartActivity("SendAndReceiveMessage", ActivityKind.Client);
            activity?.SetTag("client.handle", _handle);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);

            _logger.LogDebug("Sending message - From: {FromHandle}, To: {ToHandle}", request.FromHandle, request.ToHandle);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                await RefreshObserverIfNeeded();

                var response = await _clientGrain.SendAndReceiveMessage(request);

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                MessageProcessingDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("client.handle", _handle),
                    new KeyValuePair<string, object?>("message.from", request.FromHandle),
                    new KeyValuePair<string, object?>("message.to", request.ToHandle));

                MessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("client.handle", _handle),
                    new KeyValuePair<string, object?>("message.to", request.ToHandle));

                _logger.LogDebug("Message sent and response received - Duration: {Duration}ms", elapsed);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message from {FromHandle} to {ToHandle}",
                    request.FromHandle, request.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "send_receive_failed"));
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task SendMessage(AgentMessage request)
        {
            ThrowIfDisposed();

            using var activity = ActivitySource.StartActivity("SendMessage", ActivityKind.Producer);
            activity?.SetTag("client.handle", _handle);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);

            _logger.LogTrace("Sending message to stream - From: {FromHandle}, To: {ToHandle}",
                request.FromHandle, request.ToHandle);

            try
            {
                await RefreshObserverIfNeeded();

                await _clientGrain.SendMessage(request);

                _logger.LogTrace("Message sent to stream successfully - To: {ToHandle}", request.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to stream from {FromHandle} to {ToHandle}",
                    request.FromHandle, request.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "send_message_failed"));
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task SendEvent(AgentMessage request, string? streamName = null)
        {
            ThrowIfDisposed();

            using var activity = ActivitySource.StartActivity("SendEvent", ActivityKind.Producer);
            activity?.SetTag("client.handle", _handle);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);

            _logger.LogTrace("Sending event - From: {FromHandle}, To: {ToHandle}, StreamName: {StreamName}",
                request.FromHandle, request.ToHandle, streamName);

            try
            {
                await RefreshObserverIfNeeded();

                await _clientGrain.SendEvent(request, streamName);

                _logger.LogTrace("Event sent successfully - To: {ToHandle}, StreamName: {StreamName}",
                    request.ToHandle, streamName);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending event from {FromHandle} to {ToHandle}",
                    request.FromHandle, request.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "send_event_failed"));
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
        {
            ThrowIfDisposed();

            // Normalize handle to prevent double-prefixing (defense in depth - server also normalizes)
            if (!string.IsNullOrEmpty(agentConfiguration.Handle))
            {
                agentConfiguration.Handle = NormalizeHandle(agentConfiguration.Handle);
            }

            using var activity = ActivitySource.StartActivity("CreateAgent", ActivityKind.Internal);
            activity?.SetTag("client.handle", _handle);
            activity?.SetTag("agent.type", agentConfiguration.AgentType);
            activity?.SetTag("agent.handle", agentConfiguration.Handle);

            _logger.LogInformation("Creating agent - ClientHandle: {ClientHandle}, AgentType: {AgentType}, AgentHandle: {AgentHandle}",
                _handle, agentConfiguration.AgentType, agentConfiguration.Handle);

            try
            {
                await RefreshObserverIfNeeded();

                var health = await _clientGrain.CreateAgent(agentConfiguration);

                AgentsCreatedCounter.Add(1,
                    new KeyValuePair<string, object?>("client.handle", _handle),
                    new KeyValuePair<string, object?>("agent.type", agentConfiguration.AgentType));

                _logger.LogInformation("Agent created successfully - AgentType: {AgentType}, AgentHandle: {AgentHandle}, State: {State}",
                    agentConfiguration.AgentType, agentConfiguration.Handle, health.State);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return health;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating agent - AgentType: {AgentType}, AgentHandle: {AgentHandle}",
                    agentConfiguration.AgentType, agentConfiguration.Handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "agent_creation_failed"));
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            ThrowIfDisposed();

            using var activity = ActivitySource.StartActivity("GetAgentHealth", ActivityKind.Internal);
            activity?.SetTag("client.handle", _handle);
            activity?.SetTag("agent.handle", handle);
            activity?.SetTag("detail.level", detailLevel.ToString());

            _logger.LogDebug("Getting agent health - ClientHandle: {ClientHandle}, AgentHandle: {AgentHandle}, DetailLevel: {DetailLevel}",
                _handle, handle, detailLevel);

            try
            {
                await RefreshObserverIfNeeded();

                var agentHandle = NormalizeHandle(handle);

                _logger.LogDebug("Getting agent health - InputHandle: {InputHandle}, ResolvedGrainKey: {GrainKey}",
                    handle, agentHandle);

                var agentGrain = _clusterClient.GetGrain<IAgentGrain>(agentHandle);
                var health = await agentGrain.GetHealth(detailLevel);

                _logger.LogDebug("Got agent health - GrainKey: {GrainKey}, State: {State}, IsConfigured: {IsConfigured}",
                    agentHandle, health.State, health.IsConfigured);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return health;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting agent health - AgentHandle: {AgentHandle}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "get_health_failed"));
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<List<TrackedAgentInfo>> GetTrackedAgents()
        {
            ThrowIfDisposed();
            return await _clientGrain.GetTrackedAgents();
        }

        /// <inheritdoc/>
        public async Task<bool> IsAgentTracked(string handle)
        {
            ThrowIfDisposed();

            // Normalize handle - strip prefix if provided
            var agentHandle = StripHandlePrefix(handle);

            // Check local cache first (useful for bulk checks like 100+ lazy ChatDocks)
            if (_trackedAgentsCache != null && DateTime.UtcNow < _trackedAgentsCacheExpiry)
            {
                return _trackedAgentsCache.Contains(agentHandle) ||
                       _trackedAgentsCache.Contains($"{_handlePrefix}{agentHandle}");
            }

            // Cache miss or expired - call grain and refresh cache
            var trackedAgents = await _clientGrain.GetTrackedAgents();
            _trackedAgentsCache = trackedAgents.Select(a => a.Handle).ToHashSet(StringComparer.Ordinal);
            _trackedAgentsCacheExpiry = DateTime.UtcNow.AddSeconds(5);

            return _trackedAgentsCache.Contains(agentHandle) ||
                   _trackedAgentsCache.Contains($"{_handlePrefix}{agentHandle}");
        }

        /// <summary>
        /// IClientGrainObserver implementation - called by Orleans when a message is received.
        /// </summary>
        void IClientGrainObserver.OnMessageReceived(AgentMessage message)
        {
            // Don't process messages if disposed
            if (_disposed)
            {
                _logger.LogWarning("Received message after disposal - ignoring. Handle: {Handle}", _handle);
                return;
            }

            using var activity = ActivitySource.StartActivity("OnMessageReceived", ActivityKind.Consumer);
            activity?.SetTag("client.handle", _handle);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);

            _logger.LogInformation("Observer received message - From: {FromHandle}, To: {ToHandle}",
                message.FromHandle, message.ToHandle);

            try
            {
                // Get a snapshot of the event handlers to avoid holding the lock during invocation
                EventHandler<AgentMessage>? handlers;
                lock (_eventLock)
                {
                    handlers = _agentMessageReceived;
                }

                handlers?.Invoke(this, message);

                MessagesReceivedCounter.Add(1,
                    new KeyValuePair<string, object?>("client.handle", _handle),
                    new KeyValuePair<string, object?>("message.from", message.FromHandle));

                _logger.LogDebug("Observer processed message successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in observer processing message from: {FromHandle}", message.FromHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "observer_message_failed"));
                // Don't rethrow - we don't want to break Orleans observer callback
            }
        }

        /// <summary>
        /// Refreshes the observer subscription if it's been more than 3 minutes since the last refresh.
        /// Called as part of any client-grain interaction to keep the observer alive.
        /// If the client stops making calls, the observer naturally expires in the ObserverManager.
        /// </summary>
        private async Task RefreshObserverIfNeeded()
        {
            if (_disposed || _observerRef == null) return;
            if ((DateTime.UtcNow - _lastObserverRefresh).TotalMinutes < 3) return;

            try
            {
                await _clientGrain.Subscribe(_observerRef);
                _lastObserverRefresh = DateTime.UtcNow;
                _logger.LogDebug("Observer subscription refreshed for handle: {Handle}", _handle);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh observer subscription for handle: {Handle}", _handle);
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            using var activity = ActivitySource.StartActivity("DisposeAsync", ActivityKind.Internal);
            activity?.SetTag("client.handle", _handle);

            _logger.LogInformation("Disposing client context: {Handle}", _handle);

            try
            {
                if (_observerRef != null)
                {
                    await _clientGrain.Unsubscribe(_observerRef);
                }

                ClientsDisconnectedCounter.Add(1, new KeyValuePair<string, object?>("client.handle", _handle));

                _logger.LogInformation("Client context disposed successfully: {Handle}", _handle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing client context: {Handle}", _handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "dispose_failed"));
                // Don't throw from Dispose
            }

            // Clear event handlers
            lock (_eventLock)
            {
                _agentMessageReceived = null;
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Ensures the handle has the correct owner prefix.
        /// </summary>
        private string NormalizeHandle(string handle) => HandleUtilities.EnsurePrefix(handle, _handlePrefix);

        /// <summary>
        /// Strips our prefix from a handle if present.
        /// </summary>
        private string StripHandlePrefix(string handle) => HandleUtilities.StripPrefix(handle, _handlePrefix);

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        /// <summary>
        /// Records that the client connected successfully. Called by the factory after subscription.
        /// </summary>
        internal void RecordConnected()
        {
            ClientsConnectedCounter.Add(1, new KeyValuePair<string, object?>("client.handle", _handle));
        }
    }
}
