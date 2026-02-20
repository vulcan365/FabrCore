using Azure.Data.Tables;
using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Streaming;
using FabrCore.Host.Streaming;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FabrCore.Host.Grains
{
    internal class ClientGrain : Grain, IClientGrain
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.ClientGrain");
        private static readonly Meter Meter = new("FabrCore.Host.ClientGrain");

        // Metrics
        private static readonly Counter<long> ClientActivatedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.activated",
            description: "Number of clients activated");

        private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.messages.processed",
            description: "Number of messages processed by client");

        private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "fabrcore.client.message.duration",
            unit: "ms",
            description: "Duration of client message processing");

        private static readonly Counter<long> StreamMessagesCounter = Meter.CreateCounter<long>(
            "fabrcore.client.stream.messages",
            description: "Number of stream messages received by client");

        private static readonly Counter<long> ObserverSubscriptionsCounter = Meter.CreateCounter<long>(
            "fabrcore.client.observer.subscriptions",
            description: "Number of observer subscriptions");

        private static readonly Counter<long> ObserverNotificationsCounter = Meter.CreateCounter<long>(
            "fabrcore.client.observer.notifications",
            description: "Number of observer notifications sent");

        private static readonly Counter<long> AgentsCreatedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.agents.created",
            description: "Number of agents created by client");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.client.errors",
            description: "Number of errors encountered in client");

        private static readonly Counter<long> PendingMessagesQueuedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.pending.messages.queued",
            description: "Number of messages queued while waiting for observers");

        private static readonly Counter<long> PendingMessagesFlushedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.pending.messages.flushed",
            description: "Number of pending messages flushed to observers");

        private static readonly TimeSpan PendingMessageMaxAge = TimeSpan.FromHours(1);

        private readonly IClusterClient clusterClient;
        private readonly ILogger<ClientGrain> logger;
        private readonly ObserverManager<IClientGrainObserver> observerManager;
        private readonly Queue<AgentMessage> pendingMessages = new();
        private readonly Dictionary<string, TrackedAgentInfo> _trackedAgents = new();
        private readonly IPersistentState<ClientGrainState> _state;

        public ClientGrain(
            IClusterClient clusterClient,
            ILoggerFactory loggerFactory,
            [PersistentState("clientState", "fabrcoreStorage")]
            IPersistentState<ClientGrainState> state)
        {
            this.clusterClient = clusterClient;
            this.logger = loggerFactory.CreateLogger<ClientGrain>();
            this.observerManager = new ObserverManager<IClientGrainObserver>(TimeSpan.FromMinutes(5), logger);
            _state = state;
        }

        public Task Subscribe(IClientGrainObserver observer)
        {
            using var activity = ActivitySource.StartActivity("Subscribe", ActivityKind.Internal);
            var clientId = this.GetPrimaryKeyString();
            activity?.SetTag("client.id", clientId);

            logger.LogInformation("Observer subscribing to client: {ClientId}", clientId);

            try
            {
                observerManager.Subscribe(observer, observer);
                logger.LogInformation("Observer subscribed to client: {ClientId}", clientId);

                ObserverSubscriptionsCounter.Add(1,
                    new KeyValuePair<string, object?>("client.id", clientId),
                    new KeyValuePair<string, object?>("action", "subscribe"));

                // Flush any pending messages to the newly subscribed observer
                if (pendingMessages.Count > 0)
                {
                    var messageCount = pendingMessages.Count;
                    logger.LogInformation("Flushing {MessageCount} pending messages to observers - ClientId: {ClientId}",
                        messageCount, clientId);

                    while (pendingMessages.TryDequeue(out var message))
                    {
                        observerManager.Notify(o => o.OnMessageReceived(message));

                        ObserverNotificationsCounter.Add(1,
                            new KeyValuePair<string, object?>("client.id", clientId),
                            new KeyValuePair<string, object?>("message.from", message.FromHandle));
                    }

                    PendingMessagesFlushedCounter.Add(messageCount,
                        new KeyValuePair<string, object?>("client.id", clientId));

                    logger.LogInformation("Finished flushing pending messages - ClientId: {ClientId}", clientId);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error subscribing observer to client: {ClientId}", clientId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "subscribe_failed"),
                    new KeyValuePair<string, object?>("client.id", clientId));
                throw;
            }

            return Task.CompletedTask;
        }

        public Task Unsubscribe(IClientGrainObserver observer)
        {
            using var activity = ActivitySource.StartActivity("Unsubscribe", ActivityKind.Internal);
            var clientId = this.GetPrimaryKeyString();
            activity?.SetTag("client.id", clientId);

            logger.LogInformation("Observer unsubscribing from client: {ClientId}", clientId);

            try
            {
                observerManager.Unsubscribe(observer);
                logger.LogInformation("Observer unsubscribed from client: {ClientId}", clientId);

                ObserverSubscriptionsCounter.Add(1,
                    new KeyValuePair<string, object?>("client.id", clientId),
                    new KeyValuePair<string, object?>("action", "unsubscribe"));

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error unsubscribing observer from client: {ClientId}", clientId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "unsubscribe_failed"),
                    new KeyValuePair<string, object?>("client.id", clientId));
                throw;
            }

            return Task.CompletedTask;
        }

        public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            using var activity = ActivitySource.StartActivity("SendAndReceiveMessage", ActivityKind.Client);
            var clientId = this.GetPrimaryKeyString();

            // Resolve the ToHandle - if it contains ':', use as-is; otherwise prefix with client ID
            var resolvedToHandle = ResolveAgentHandle(request.ToHandle, clientId);

            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", resolvedToHandle);
            activity?.SetTag("client.id", clientId);
            activity?.SetTag("from.type", "Client");  // Client sending to agent
            activity?.SetTag("to.type", "Agent");

            // Update the request with the resolved handle
            request.ToHandle = resolvedToHandle;

            logger.LogDebug("Client sending message - From: {FromHandle}, To: {ToHandle}",
                request.FromHandle, resolvedToHandle);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var agentProxy = clusterClient.GetGrain<IAgentGrain>(resolvedToHandle);
                var response = await agentProxy.OnMessage(request);

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                MessageProcessingDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("message.from", request.FromHandle),
                    new KeyValuePair<string, object?>("message.to", resolvedToHandle),
                    new KeyValuePair<string, object?>("client.id", clientId));

                MessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("message.from", request.FromHandle),
                    new KeyValuePair<string, object?>("message.to", resolvedToHandle),
                    new KeyValuePair<string, object?>("client.id", clientId));

                logger.LogDebug("Client message completed - Response received from: {ToHandle}", resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending message from client - From: {FromHandle}, To: {ToHandle}",
                    request.FromHandle, resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "send_receive_failed"),
                    new KeyValuePair<string, object?>("client.id", this.GetPrimaryKeyString()));
                throw;
            }
        }

        public async Task SendMessage(AgentMessage request)
        {
            using var activity = ActivitySource.StartActivity("SendMessage", ActivityKind.Producer);
            var clientId = this.GetPrimaryKeyString();

            // Resolve the ToHandle - if it contains ':', use as-is; otherwise prefix with client ID
            var resolvedToHandle = ResolveAgentHandle(request.ToHandle, clientId);

            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", resolvedToHandle);
            activity?.SetTag("client.id", clientId);
            activity?.SetTag("from.type", "Client");  // Client sending to agent
            activity?.SetTag("to.type", "Agent");
            activity?.SetTag("stream.provider", StreamConstants.ProviderName);
            activity?.SetTag("stream.namespace", StreamConstants.AgentChatNamespace);

            // Update the request with the resolved handle
            request.ToHandle = resolvedToHandle;

            logger.LogTrace("Client sending message to stream - From: {FromHandle}, To: {ToHandle}",
                request.FromHandle, resolvedToHandle);

            try
            {
                var stream = clusterClient.GetAgentChatStream(resolvedToHandle);
                await stream.OnNextAsync(request);

                logger.LogTrace("Client message sent to stream for: {ToHandle}", resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending message to stream from client for: {ToHandle}", resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_send_failed"),
                    new KeyValuePair<string, object?>("client.id", this.GetPrimaryKeyString()));
                throw;
            }
        }

        public async Task SendEvent(AgentMessage request, string? streamName = null)
        {
            using var activity = ActivitySource.StartActivity("SendEvent", ActivityKind.Producer);
            var clientId = this.GetPrimaryKeyString();

            activity?.SetTag("client.id", clientId);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("stream.provider", StreamConstants.ProviderName);
            activity?.SetTag("stream.namespace", StreamConstants.AgentEventNamespace);

            try
            {
                if (streamName != null)
                {
                    // Named event stream — publish directly, no handle normalization
                    activity?.SetTag("stream.name", streamName);

                    logger.LogTrace("Client sending event to named stream - From: {FromHandle}, StreamName: {StreamName}",
                        request.FromHandle, streamName);

                    var stream = clusterClient.GetAgentEventStream(streamName);
                    await stream.OnNextAsync(request);

                    logger.LogTrace("Client event sent to named stream: {StreamName}", streamName);
                }
                else
                {
                    // Default agent event stream — resolve handle
                    var resolvedToHandle = ResolveAgentHandle(request.ToHandle, clientId);
                    request.ToHandle = resolvedToHandle;

                    activity?.SetTag("message.to", resolvedToHandle);

                    logger.LogTrace("Client sending event to stream - From: {FromHandle}, To: {ToHandle}",
                        request.FromHandle, resolvedToHandle);

                    var stream = clusterClient.GetAgentEventStream(resolvedToHandle);
                    await stream.OnNextAsync(request);

                    logger.LogTrace("Client event sent to stream for: {ToHandle}", resolvedToHandle);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending event from client - ClientId: {ClientId}", clientId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_event_send_failed"),
                    new KeyValuePair<string, object?>("client.id", clientId));
                throw;
            }
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("OnActivateAsync", ActivityKind.Internal);
            var clientId = this.GetPrimaryKeyString();
            activity?.SetTag("client.id", clientId);

            logger.LogInformation("Client activating: {ClientId}", clientId);

            try
            {
                await base.OnActivateAsync(cancellationToken);

                // Restore tracked agents from persisted state
                if (_state.State.TrackedAgents.Count > 0)
                {
                    foreach (var kvp in _state.State.TrackedAgents)
                    {
                        _trackedAgents[kvp.Key] = kvp.Value;
                    }
                    logger.LogInformation("Restored {Count} tracked agents for client: {ClientId}",
                        _trackedAgents.Count, clientId);
                }

                // Restore pending messages with age-based expiry check
                if (_state.State.PendingMessages.Count > 0)
                {
                    var persistedAt = _state.State.PendingMessagesPersisted;
                    var shouldRestore = true;

                    if (persistedAt.HasValue)
                    {
                        var age = DateTime.UtcNow - persistedAt.Value;
                        if (age > PendingMessageMaxAge)
                        {
                            logger.LogWarning("Discarding {Count} stale pending messages for client {ClientId} - age: {Age}",
                                _state.State.PendingMessages.Count, clientId, age);
                            _state.State.PendingMessages.Clear();
                            _state.State.PendingMessagesPersisted = null;
                            shouldRestore = false;
                        }
                    }

                    if (shouldRestore)
                    {
                        foreach (var msg in _state.State.PendingMessages)
                        {
                            pendingMessages.Enqueue(msg);
                        }
                        logger.LogInformation("Restored {Count} pending messages for client: {ClientId}",
                            pendingMessages.Count, clientId);
                    }
                }

                await CreateStreams();
                await RegisterWithManagementGrain();

                logger.LogInformation("Client activated and streams created: {ClientId}", clientId);

                ClientActivatedCounter.Add(1, new KeyValuePair<string, object?>("client.id", clientId));

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error activating client: {ClientId}", clientId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "activation_failed"),
                    new KeyValuePair<string, object?>("client.id", clientId));
                throw;
            }
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            var clientId = this.GetPrimaryKeyString();
            logger.LogInformation("Client deactivating: {ClientId}, Reason: {Reason}",
                clientId, reason.Description);

            // Persist all state before deactivation
            try
            {
                // Copy tracked agents to state
                _state.State.TrackedAgents.Clear();
                foreach (var kvp in _trackedAgents)
                {
                    _state.State.TrackedAgents[kvp.Key] = kvp.Value;
                }

                // Copy pending messages to state (Queue to List) with timestamp
                _state.State.PendingMessages.Clear();
                _state.State.PendingMessages.AddRange(pendingMessages);
                _state.State.PendingMessagesPersisted = pendingMessages.Count > 0 ? DateTime.UtcNow : null;

                _state.State.LastModified = DateTime.UtcNow;
                await _state.WriteStateAsync();

                logger.LogInformation("Persisted state on deactivation - ClientId: {ClientId}, TrackedAgents: {AgentCount}, PendingMessages: {MessageCount}",
                    clientId, _trackedAgents.Count, pendingMessages.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist client state on deactivation: {ClientId}", clientId);
            }

            try
            {
                var registry = GrainFactory.GetGrain<IAgentManagementGrain>(0);
                await registry.DeactivateClient(clientId, reason.Description);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deactivate client in registry: {ClientId}", clientId);
            }

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        private async Task RegisterWithManagementGrain()
        {
            var clientId = this.GetPrimaryKeyString();
            try
            {
                var registry = GrainFactory.GetGrain<IAgentManagementGrain>(0);
                await registry.RegisterClient(clientId);

                logger.LogInformation("Registered client with management grain: {ClientId}", clientId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register client with management grain: {ClientId}", clientId);
            }
        }

        private async Task CreateStreams()
        {
            using var activity = ActivitySource.StartActivity("CreateStreams", ActivityKind.Internal);
            var clientId = this.GetPrimaryKeyString();
            var streamName = StreamName.ForAgentChat(clientId);

            activity?.SetTag("client.id", clientId);
            activity?.SetTag("stream.name", streamName.ToString());

            logger.LogDebug("Creating streams for client: {ClientId}", clientId);

            try
            {
                var stream = this.GetStream<AgentMessage>(streamName);
                var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);

                var handles = await stream.GetAllSubscriptionHandles();
                logger.LogTrace("Found {HandleCount} existing subscription handles for client stream: {ClientId}",
                    handles.Count, clientId);

                foreach (var handle in handles)
                {
                    if (handle.StreamId == streamId)
                    {
                        await handle.UnsubscribeAsync();
                        logger.LogTrace("Unsubscribed from existing handle for client: {ClientId}", clientId);
                    }
                }

                StreamSubscriptionHandle<AgentMessage> subscription = await stream.SubscribeAsync(ReceivedStreamingMessage);
                logger.LogInformation("Client subscribed to stream: {StreamName}", streamName);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating streams for client: {ClientId}", clientId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_creation_failed"),
                    new KeyValuePair<string, object?>("client.id", clientId));
                throw;
            }
        }

        private Task ReceivedStreamingMessage(AgentMessage request, StreamSequenceToken? token = null)
        {
            using var activity = ActivitySource.StartActivity("ReceivedStreamingMessage", ActivityKind.Consumer);
            var clientId = this.GetPrimaryKeyString();

            activity?.SetTag("client.id", clientId);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);
            activity?.SetTag("from.type", "Agent");  // Agent sending to client
            activity?.SetTag("to.type", "Client");
            if (token != null)
            {
                activity?.SetTag("stream.sequence", token.SequenceNumber);
            }

            logger.LogInformation("Client received message - ClientId: {ClientId}, From: {FromHandle}",
                clientId, request.FromHandle);

            StreamMessagesCounter.Add(1,
                new KeyValuePair<string, object?>("client.id", clientId),
                new KeyValuePair<string, object?>("message.from", request.FromHandle));

            try
            {
                // If no observers are subscribed, queue the message for later delivery
                if (observerManager.Count == 0)
                {
                    pendingMessages.Enqueue(request);
                    logger.LogInformation("No observers subscribed, message queued - ClientId: {ClientId}, QueueLength: {QueueLength}",
                        clientId, pendingMessages.Count);

                    PendingMessagesQueuedCounter.Add(1,
                        new KeyValuePair<string, object?>("client.id", clientId),
                        new KeyValuePair<string, object?>("message.from", request.FromHandle));

                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
                    // Notify all observers
                    observerManager.Notify(observer => observer.OnMessageReceived(request));

                    ObserverNotificationsCounter.Add(1,
                        new KeyValuePair<string, object?>("client.id", clientId),
                        new KeyValuePair<string, object?>("message.from", request.FromHandle));

                    logger.LogInformation("Client observers notified - ClientId: {ClientId}", clientId);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing streaming message for client: {ClientId}", clientId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "observer_notification_failed"),
                    new KeyValuePair<string, object?>("client.id", clientId));
                throw;
            }

            return Task.CompletedTask;
        }

        public async Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
        {
            using var activity = ActivitySource.StartActivity("CreateAgent", ActivityKind.Internal);
            var clientId = this.GetPrimaryKeyString();
            var originalHandle = agentConfiguration.Handle;

            // Normalize handle to prevent double-prefixing (e.g., if caller passes "owner:agent")
            var handlePrefix = HandleUtilities.BuildPrefix(clientId);
            agentConfiguration.Handle = HandleUtilities.EnsurePrefix(
                agentConfiguration.Handle ?? throw new ArgumentException("Handle is required"),
                handlePrefix);

            activity?.SetTag("client.id", clientId);
            activity?.SetTag("agent.handle", agentConfiguration.Handle);
            activity?.SetTag("agent.type", agentConfiguration.AgentType);

            logger.LogInformation("Client creating agent - ClientId: {ClientId}, AgentType: {AgentType}, Handle: {Handle}",
                clientId, agentConfiguration.AgentType, agentConfiguration.Handle);

            try
            {
                var proxy = clusterClient.GetGrain<IAgentGrain>(agentConfiguration.Handle);
                AgentHealthStatus health;

                // OPTIMIZATION: If agent is already tracked and not forcing reconfigure, check health first (lighter than ConfigureAgent)
                if (_trackedAgents.ContainsKey(agentConfiguration.Handle) && !agentConfiguration.ForceReconfigure)
                {
                    activity?.SetTag("agent.tracked", true);
                    health = await TryGetHealthOrConfigure(proxy, agentConfiguration);
                }
                else
                {
                    // New agent or force reconfigure - must configure
                    activity?.SetTag("agent.tracked", false);
                    activity?.SetTag("agent.force_reconfigure", agentConfiguration.ForceReconfigure);
                    health = await proxy.ConfigureAgent(agentConfiguration, agentConfiguration.ForceReconfigure);

                    // Track the new agent
                    _trackedAgents[agentConfiguration.Handle] = new TrackedAgentInfo(
                        agentConfiguration.Handle,
                        agentConfiguration.AgentType ?? "Unknown"
                    );

                    // Persist tracked agents immediately
                    _state.State.TrackedAgents[agentConfiguration.Handle] = _trackedAgents[agentConfiguration.Handle];
                    _state.State.LastModified = DateTime.UtcNow;
                    await _state.WriteStateAsync();

                    logger.LogDebug("Persisted tracked agent - ClientId: {ClientId}, Handle: {Handle}",
                        clientId, agentConfiguration.Handle);

                    AgentsCreatedCounter.Add(1,
                        new KeyValuePair<string, object?>("client.id", clientId),
                        new KeyValuePair<string, object?>("agent.type", agentConfiguration.AgentType),
                        new KeyValuePair<string, object?>("agent.handle", agentConfiguration.Handle));
                }

                logger.LogInformation("Client agent ready - ClientId: {ClientId}, Handle: {Handle}, State: {State}",
                    clientId, agentConfiguration.Handle, health.State);

                activity?.SetStatus(ActivityStatusCode.Ok);
                return health;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating agent for client - ClientId: {ClientId}, AgentType: {AgentType}",
                    clientId, agentConfiguration.AgentType);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "agent_creation_failed"),
                    new KeyValuePair<string, object?>("client.id", clientId));
                throw;
            }
        }

        /// <summary>
        /// For tracked agents: try GetHealth first, only ConfigureAgent if NotConfigured or on error.
        /// </summary>
        private async Task<AgentHealthStatus> TryGetHealthOrConfigure(
            IAgentGrain proxy,
            AgentConfiguration config)
        {
            try
            {
                var health = await proxy.GetHealth(HealthDetailLevel.Basic);

                if (health.State == HealthState.NotConfigured)
                {
                    // Agent was deactivated, needs reconfiguration
                    logger.LogDebug("Tracked agent not configured, reconfiguring: {Handle}", config.Handle);
                    return await proxy.ConfigureAgent(config);
                }

                // Agent is configured (Healthy, Degraded, or Unhealthy) - return as-is
                logger.LogDebug("Tracked agent health check passed: {Handle}, State: {State}",
                    config.Handle, health.State);
                return health;
            }
            catch (Exception ex)
            {
                // GetHealth failed - fallback to ConfigureAgent
                logger.LogWarning(ex, "Health check failed for {Handle}, falling back to ConfigureAgent", config.Handle);
                return await proxy.ConfigureAgent(config);
            }
        }

        public Task<List<TrackedAgentInfo>> GetTrackedAgents()
        {
            return Task.FromResult(_trackedAgents.Values.ToList());
        }

        public Task<bool> IsAgentTracked(string handle)
        {
            var clientId = this.GetPrimaryKeyString();
            var fullHandle = ResolveAgentHandle(handle, clientId);

            // O(1) dictionary lookup
            return Task.FromResult(_trackedAgents.ContainsKey(fullHandle));
        }

        /// <summary>
        /// Resolves an agent handle. If it contains ':', uses as-is (already qualified).
        /// Otherwise, prefixes with the client ID.
        /// </summary>
        private static string ResolveAgentHandle(string? handle, string clientId)
        {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("Handle cannot be null or empty", nameof(handle));

            var prefix = HandleUtilities.BuildPrefix(clientId);
            return HandleUtilities.EnsurePrefix(handle, prefix);
        }
    }
}
