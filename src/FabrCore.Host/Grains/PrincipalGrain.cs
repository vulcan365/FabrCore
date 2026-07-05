using Azure.Data.Tables;
using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Monitoring;
using FabrCore.Core.Streaming;
using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FabrCore.Host.Grains
{
    internal class PrincipalGrain : Grain, IPrincipalGrain
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.PrincipalGrain");
        private static readonly Meter Meter = new("FabrCore.Host.PrincipalGrain");

        // Metrics
        private static readonly Counter<long> PrincipalActivatedCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.activated",
            description: "Number of principals activated");

        private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.messages.processed",
            description: "Number of messages processed by principal");

        private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "fabrcore.principal.message.duration",
            unit: "ms",
            description: "Duration of principal message processing");

        private static readonly Counter<long> StreamMessagesCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.stream.messages",
            description: "Number of stream messages received by principal");

        private static readonly Counter<long> ObserverSubscriptionsCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.observer.subscriptions",
            description: "Number of observer subscriptions");

        private static readonly Counter<long> ObserverNotificationsCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.observer.notifications",
            description: "Number of observer notifications sent");

        private static readonly Counter<long> AgentsCreatedCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.agents.created",
            description: "Number of agents created by principal");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.errors",
            description: "Number of errors encountered in principal");

        private static readonly Counter<long> PendingMessagesQueuedCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.pending.messages.queued",
            description: "Number of messages queued while waiting for observers");

        private static readonly Counter<long> PendingMessagesFlushedCounter = Meter.CreateCounter<long>(
            "fabrcore.principal.pending.messages.flushed",
            description: "Number of pending messages flushed to observers");

        private readonly IClusterClient clusterClient;
        private readonly ILogger<PrincipalGrain> logger;
        private readonly IFabrCoreAgentService _agentService;
        private readonly IAclProvider _aclProvider;
        private readonly IAgentMessageMonitor _messageMonitor;
        private readonly VerifiableExecutionRecorder _verifiableExecution;
        private readonly ObserverManager<IPrincipalGrainObserver> observerManager;
        private readonly Queue<AgentMessage> pendingMessages = new();
        private readonly Dictionary<string, TrackedAgentInfo> _trackedAgents = new();
        private readonly IPersistentState<PrincipalGrainState> _state;
        private readonly PrincipalGrainOptions _grainOptions;

        public PrincipalGrain(
            IClusterClient clusterClient,
            ILoggerFactory loggerFactory,
            IFabrCoreAgentService agentService,
            IAclProvider aclProvider,
            IAgentMessageMonitor messageMonitor,
            VerifiableExecutionRecorder verifiableExecution,
            Microsoft.Extensions.Options.IOptions<PrincipalGrainOptions> grainOptions,
            [PersistentState("principalState", FabrCoreOrleansConstants.StorageProviderName)]
            IPersistentState<PrincipalGrainState> state)
        {
            this.clusterClient = clusterClient;
            this.logger = loggerFactory.CreateLogger<PrincipalGrain>();
            this._grainOptions = grainOptions.Value;
            _agentService = agentService;
            _aclProvider = aclProvider;
            _messageMonitor = messageMonitor;
            _verifiableExecution = verifiableExecution;
            this.observerManager = new ObserverManager<IPrincipalGrainObserver>(TimeSpan.FromMinutes(5), logger);
            _state = state;
        }

        public Task Subscribe(IPrincipalGrainObserver observer)
        {
            using var activity = ActivitySource.StartActivity("Subscribe", ActivityKind.Internal);
            var principalHandle = this.GetPrimaryKeyString();
            activity?.SetTag("principal.handle", principalHandle);

            logger.LogInformation("Observer subscribing to principal: {PrincipalHandle}", principalHandle);

            try
            {
                observerManager.Subscribe(observer, observer);
                logger.LogInformation("Observer subscribed to principal: {PrincipalHandle}", principalHandle);

                ObserverSubscriptionsCounter.Add(1,
                    new KeyValuePair<string, object?>("action", "subscribe"));

                // Flush any pending messages to the newly subscribed observer
                if (pendingMessages.Count > 0)
                {
                    var messageCount = pendingMessages.Count;
                    logger.LogInformation("Flushing {MessageCount} pending messages to observers - PrincipalHandle: {PrincipalHandle}",
                        messageCount, principalHandle);

                    while (pendingMessages.TryDequeue(out var message))
                    {
                        observerManager.Notify(o => o.OnMessageReceived(message));

                        ObserverNotificationsCounter.Add(1);
                    }

                    PendingMessagesFlushedCounter.Add(messageCount);

                    logger.LogInformation("Finished flushing pending messages - PrincipalHandle: {PrincipalHandle}", principalHandle);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error subscribing observer to principal: {PrincipalHandle}", principalHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "subscribe_failed"));
                throw;
            }

            return Task.CompletedTask;
        }

        public Task Unsubscribe(IPrincipalGrainObserver observer)
        {
            using var activity = ActivitySource.StartActivity("Unsubscribe", ActivityKind.Internal);
            var principalHandle = this.GetPrimaryKeyString();
            activity?.SetTag("principal.handle", principalHandle);

            logger.LogInformation("Observer unsubscribing from principal: {PrincipalHandle}", principalHandle);

            try
            {
                observerManager.Unsubscribe(observer);
                logger.LogInformation("Observer unsubscribed from principal: {PrincipalHandle}", principalHandle);

                ObserverSubscriptionsCounter.Add(1,
                    new KeyValuePair<string, object?>("action", "unsubscribe"));

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error unsubscribing observer from principal: {PrincipalHandle}", principalHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "unsubscribe_failed"));
                throw;
            }

            return Task.CompletedTask;
        }

        public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            using var activity = ActivitySource.StartActivity("SendAndReceiveMessage", ActivityKind.Client);
            var principalHandle = this.GetPrimaryKeyString();

            // Resolve the ToHandle - if it contains ':', use as-is; otherwise prefix with principal handle
            var resolvedToHandle = ResolveAgentHandle(request.ToHandle, principalHandle);

            // ACL check for cross-principal access
            await AuthorizeOrThrow(resolvedToHandle, AclPermission.Message);

            // Defense-in-depth: ensure FromHandle is set so the agent can route responses back
            if (string.IsNullOrEmpty(request.FromHandle))
                request.FromHandle = principalHandle;

            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", resolvedToHandle);
            activity?.SetTag("principal.handle", principalHandle);
            activity?.SetTag("from.type", "Principal");
            activity?.SetTag("to.type", "Agent");

            // Update the request with the resolved handle
            request.ToHandle = resolvedToHandle;
            request.StampFromActivity(activity);
            request.VerifiableExecution = await RecordMessageEvidenceAsync(
                ExecutionRecordKind.AgentDispatch,
                principalHandle,
                request,
                "principal.message.request_response.dispatch");

            logger.LogDebug("Principal sending message - From: {FromHandle}, To: {ToHandle}",
                request.FromHandle, resolvedToHandle);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var agentProxy = clusterClient.GetGrain<IAgentGrain>(resolvedToHandle);
                var response = await agentProxy.OnMessage(request);

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                // message.from/to/principal.handle dropped — all unbounded handles. Retained on activity span.
                MessageProcessingDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("message.kind", request.Kind.ToString()));

                MessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("message.kind", request.Kind.ToString()));

                logger.LogDebug("Principal message completed - Response received from: {ToHandle}", resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending message from principal - From: {FromHandle}, To: {ToHandle}",
                    request.FromHandle, resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "send_receive_failed"));
                throw;
            }
        }

        public async Task SendMessage(AgentMessage request)
        {
            using var activity = ActivitySource.StartActivity("SendMessage", ActivityKind.Producer);
            var principalHandle = this.GetPrimaryKeyString();

            // Resolve the ToHandle - if it contains ':', use as-is; otherwise prefix with principal handle
            var resolvedToHandle = ResolveAgentHandle(request.ToHandle, principalHandle);

            // ACL check for cross-principal access
            await AuthorizeOrThrow(resolvedToHandle, AclPermission.Message);

            // Defense-in-depth: ensure FromHandle is set so the agent can route responses back
            if (string.IsNullOrEmpty(request.FromHandle))
                request.FromHandle = principalHandle;

            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", resolvedToHandle);
            activity?.SetTag("principal.handle", principalHandle);
            activity?.SetTag("from.type", "Principal");
            activity?.SetTag("to.type", "Agent");
            activity?.SetTag("stream.provider", StreamConstants.ProviderName);
            activity?.SetTag("stream.namespace", StreamConstants.AgentChatNamespace);

            // Update the request with the resolved handle
            request.ToHandle = resolvedToHandle;
            request.StampFromActivity(activity);
            request.VerifiableExecution = await RecordMessageEvidenceAsync(
                ExecutionRecordKind.AgentDispatch,
                principalHandle,
                request,
                "principal.message.dispatch");

            logger.LogTrace("Principal sending message to stream - From: {FromHandle}, To: {ToHandle}",
                request.FromHandle, resolvedToHandle);

            try
            {
                // AgentChat uses [ImplicitStreamSubscription] — Orleans auto-activates the
                // target AgentGrain on first message. If unconfigured, the grain sends _error back.
                var stream = clusterClient.GetAgentChatStream(resolvedToHandle);
                await stream.OnNextAsync(request);

                logger.LogTrace("Principal message sent to stream for: {ToHandle}", resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending message to stream from principal for: {ToHandle}", resolvedToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_send_failed"));
                throw;
            }
        }

        public async Task SendEvent(EventMessage request)
        {
            using var activity = ActivitySource.StartActivity("SendEvent", ActivityKind.Producer);
            var principalHandle = this.GetPrimaryKeyString();

            activity?.SetTag("principal.handle", principalHandle);
            activity?.SetTag("event.source", request.Source);
            activity?.SetTag("event.type", request.Type);
            activity?.SetTag("stream.provider", StreamConstants.ProviderName);

            try
            {
                if (string.IsNullOrWhiteSpace(request.Namespace))
                {
                    // Default agent event stream — resolve handle from Channel
                    var resolvedChannel = ResolveAgentHandle(request.Channel, principalHandle);

                    // ACL check for cross-principal event delivery
                    await AuthorizeOrThrow(resolvedChannel, AclPermission.Message);

                    request.Channel = resolvedChannel;
                }

                var streamName = EventStreamSubscription.ToStreamName(request);
                activity?.SetTag("event.namespace", request.Namespace);
                activity?.SetTag("event.channel", request.Channel);
                activity?.SetTag("stream.name", streamName.ToString());
                activity?.SetTag("stream.namespace", streamName.Namespace);
                request.StampFromActivity(activity);
                request.VerifiableExecution = await RecordEventEvidenceAsync(
                    ExecutionRecordKind.EventPublished,
                    principalHandle,
                    request,
                    "principal.event.published");

                logger.LogTrace("Principal sending event to stream - Source: {Source}, StreamName: {StreamName}",
                    request.Source, streamName);

                var stream = clusterClient.GetStream<EventMessage>(streamName);
                await stream.OnNextAsync(request);

                logger.LogTrace("Principal event sent to stream: {StreamName}", streamName);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending event from principal - PrincipalHandle: {PrincipalHandle}", principalHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_event_send_failed"));
                throw;
            }
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("OnActivateAsync", ActivityKind.Internal);
            var principalHandle = this.GetPrimaryKeyString();
            activity?.SetTag("principal.handle", principalHandle);

            logger.LogInformation("Principal activating: {PrincipalHandle}", principalHandle);

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
                    logger.LogInformation("Restored {Count} tracked agents for principal: {PrincipalHandle}",
                        _trackedAgents.Count, principalHandle);
                }

                // Restore pending messages with age-based expiry check
                if (_state.State.PendingMessages.Count > 0)
                {
                    var persistedAt = _state.State.PendingMessagesPersisted;
                    var shouldRestore = true;

                    if (persistedAt.HasValue)
                    {
                        var age = DateTime.UtcNow - persistedAt.Value;
                        if (age > _grainOptions.PendingMessageMaxAge)
                        {
                            logger.LogWarning("Discarding {Count} stale pending messages for principal {PrincipalHandle} - age: {Age}",
                                _state.State.PendingMessages.Count, principalHandle, age);
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
                        logger.LogInformation("Restored {Count} pending messages for principal: {PrincipalHandle}",
                            pendingMessages.Count, principalHandle);
                    }
                }

                await CreateStreams();
                await RegisterWithManagement();

                logger.LogInformation("Principal activated and streams created: {PrincipalHandle}", principalHandle);

                PrincipalActivatedCounter.Add(1);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error activating principal: {PrincipalHandle}", principalHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "activation_failed"));
                throw;
            }
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            var principalHandle = this.GetPrimaryKeyString();
            logger.LogInformation("Principal deactivating: {PrincipalHandle}, Reason: {Reason}",
                principalHandle, reason.Description);

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

                logger.LogInformation("Persisted state on deactivation - PrincipalHandle: {PrincipalHandle}, TrackedAgents: {AgentCount}, PendingMessages: {MessageCount}",
                    principalHandle, _trackedAgents.Count, pendingMessages.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist principal state on deactivation: {PrincipalHandle}", principalHandle);
            }

            try
            {
                await _agentService.DeactivatePrincipalAsync(principalHandle, reason.Description);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deactivate principal in registry: {PrincipalHandle}", principalHandle);
            }

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        private async Task RegisterWithManagement()
        {
            var principalHandle = this.GetPrimaryKeyString();
            try
            {
                await _agentService.RegisterPrincipalAsync(principalHandle);

                logger.LogInformation("Registered principal with management provider: {PrincipalHandle}", principalHandle);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register principal with management provider: {PrincipalHandle}", principalHandle);
            }
        }

        private async Task CreateStreams()
        {
            using var activity = ActivitySource.StartActivity("CreateStreams", ActivityKind.Internal);
            var principalHandle = this.GetPrimaryKeyString();
            var streamName = StreamName.ForAgentChat(principalHandle);

            activity?.SetTag("principal.handle", principalHandle);
            activity?.SetTag("stream.name", streamName.ToString());

            logger.LogDebug("Creating streams for principal: {PrincipalHandle}", principalHandle);

            try
            {
                var stream = this.GetStream<AgentMessage>(streamName);
                var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);

                var handles = await stream.GetAllSubscriptionHandles();
                logger.LogTrace("Found {HandleCount} existing subscription handles for principal stream: {PrincipalHandle}",
                    handles.Count, principalHandle);

                foreach (var handle in handles)
                {
                    if (handle.StreamId == streamId)
                    {
                        await handle.UnsubscribeAsync();
                        logger.LogTrace("Unsubscribed from existing handle for principal: {PrincipalHandle}", principalHandle);
                    }
                }

                StreamSubscriptionHandle<AgentMessage> subscription = await stream.SubscribeAsync(ReceivedStreamingMessage);
                logger.LogInformation("Principal subscribed to stream: {StreamName}", streamName);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating streams for principal: {PrincipalHandle}", principalHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_creation_failed"));
                throw;
            }
        }

        private Task<VerifiableExecutionEnvelope?> RecordMessageEvidenceAsync(
            ExecutionRecordKind kind,
            string principalHandle,
            AgentMessage message,
            string subject)
        {
            var record = new VerifiableExecutionRecord
            {
                Kind = kind,
                TraceId = message.TraceId,
                SpanId = message.SpanId,
                ParentSpanId = message.ParentSpanId,
                UserHandle = principalHandle,
                AgentHandle = principalHandle,
                Subject = subject,
                PayloadHash = DigestText(JsonSerializer.Serialize(new
                {
                    message.Message,
                    message.DataType,
                    DataHash = DigestBytes(message.Data),
                    message.Files
                })),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["message.id"] = message.Id,
                    ["message.from"] = message.FromHandle,
                    ["message.to"] = message.ToHandle,
                    ["message.channel"] = message.Channel,
                    ["message.type"] = message.MessageType,
                    ["message.kind"] = message.Kind.ToString()
                }
            };

            return _verifiableExecution.RecordAsync(record);
        }

        private Task<VerifiableExecutionEnvelope?> RecordEventEvidenceAsync(
            ExecutionRecordKind kind,
            string principalHandle,
            EventMessage message,
            string subject)
        {
            var record = new VerifiableExecutionRecord
            {
                Kind = kind,
                TraceId = message.TraceId,
                SpanId = message.SpanId,
                ParentSpanId = message.ParentSpanId,
                UserHandle = principalHandle,
                AgentHandle = principalHandle,
                Subject = subject,
                PayloadHash = DigestText(JsonSerializer.Serialize(new
                {
                    message.Data,
                    message.DataContentType,
                    BinaryHash = DigestBytes(message.BinaryData)
                })),
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["event.id"] = message.Id,
                    ["event.type"] = message.Type,
                    ["event.source"] = message.Source,
                    ["event.subject"] = message.Subject,
                    ["event.namespace"] = message.Namespace,
                    ["event.channel"] = message.Channel
                }
            };

            return _verifiableExecution.RecordAsync(record);
        }

        private static string DigestText(string? text)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();

        private static string DigestBytes(byte[]? bytes)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes ?? Array.Empty<byte>())).ToLowerInvariant();

        private Task ReceivedStreamingMessage(AgentMessage request, StreamSequenceToken? token = null)
        {
            using var activity = ActivitySource.StartActivity("ReceivedStreamingMessage", ActivityKind.Consumer);
            var principalHandle = this.GetPrimaryKeyString();

            activity?.SetTag("principal.handle", principalHandle);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);
            activity?.SetTag("from.type", "Agent");  // Agent sending to principal
            activity?.SetTag("to.type", "Principal");
            if (token != null)
            {
                activity?.SetTag("stream.sequence", token.SequenceNumber);
            }

            logger.LogInformation("Principal received message - PrincipalHandle: {PrincipalHandle}, From: {FromHandle}",
                principalHandle, request.FromHandle);

            StreamMessagesCounter.Add(1);

            // Record inbound message to principal in the message monitor
            _messageMonitor.RecordMessageAsync(new MonitoredMessage
            {
                AgentHandle = principalHandle,
                FromHandle = request.FromHandle,
                ToHandle = request.ToHandle,
                OnBehalfOfHandle = request.OnBehalfOfHandle,
                DeliverToHandle = request.DeliverToHandle,
                Channel = request.Channel,
                Message = request.Message,
                MessageType = request.MessageType,
                Kind = request.Kind,
                DataType = request.DataType,
                Files = request.Files,
                State = request.State,
                Args = request.Args,
                Direction = MessageDirection.Inbound,
                TraceId = request.TraceId,
                LlmUsage = LlmUsageInfo.FromArgs(request.Args),
                VerifiableExecutionId = request.VerifiableExecution?.RecordId,
                SignatureDigest = request.VerifiableExecution?.CurrentSignatureDigest,
                VerificationStatus = request.VerifiableExecution?.SignerIdentityKind == VerifiableExecutionSignerIdentityKind.None ? "Unsigned" : "Signed"
            }).TrackRecording(logger, ErrorCounter, "RecordMessage.PrincipalInbound", principalHandle);

            try
            {
                // If no observers are subscribed, queue the message for later delivery
                if (observerManager.Count == 0)
                {
                    pendingMessages.Enqueue(request);
                    logger.LogInformation("No observers subscribed, message queued - PrincipalHandle: {PrincipalHandle}, QueueLength: {QueueLength}",
                        principalHandle, pendingMessages.Count);

                    PendingMessagesQueuedCounter.Add(1);

                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
                    // Notify all observers
                    observerManager.Notify(observer => observer.OnMessageReceived(request));

                    ObserverNotificationsCounter.Add(1);

                    logger.LogInformation("Principal observers notified - PrincipalHandle: {PrincipalHandle}", principalHandle);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing streaming message for principal: {PrincipalHandle}", principalHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "observer_notification_failed"));
                throw;
            }

            return Task.CompletedTask;
        }

        public async Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
        {
            using var activity = ActivitySource.StartActivity("CreateAgent", ActivityKind.Internal);
            var principalHandle = this.GetPrimaryKeyString();
            var originalHandle = agentConfiguration.Handle;

            // Normalize handle to prevent double-prefixing (e.g., if caller passes "principalHandle:agentHandle")
            var handlePrefix = HandleUtilities.BuildPrefix(principalHandle);
            agentConfiguration.Handle = HandleUtilities.EnsurePrefix(
                agentConfiguration.Handle ?? throw new ArgumentException("Handle is required"),
                handlePrefix);

            // ACL check for cross-principal agent creation
            await AuthorizeOrThrow(agentConfiguration.Handle, AclPermission.Configure);

            activity?.SetTag("principal.handle", principalHandle);
            activity?.SetTag("agent.handle", agentConfiguration.Handle);
            activity?.SetTag("agent.type", agentConfiguration.AgentType);

            logger.LogInformation("Principal creating agent - PrincipalHandle: {PrincipalHandle}, AgentType: {AgentType}, Handle: {Handle}",
                principalHandle, agentConfiguration.AgentType, agentConfiguration.Handle);

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

                    logger.LogDebug("Persisted tracked agent - PrincipalHandle: {PrincipalHandle}, Handle: {Handle}",
                        principalHandle, agentConfiguration.Handle);

                    AgentsCreatedCounter.Add(1,
                        new KeyValuePair<string, object?>("agent.type", agentConfiguration.AgentType));
                }

                logger.LogInformation("Principal agent ready - PrincipalHandle: {PrincipalHandle}, Handle: {Handle}, State: {State}",
                    principalHandle, agentConfiguration.Handle, health.State);

                activity?.SetStatus(ActivityStatusCode.Ok);
                return health;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating agent for principal - PrincipalHandle: {PrincipalHandle}, AgentType: {AgentType}",
                    principalHandle, agentConfiguration.AgentType);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "agent_creation_failed"));
                throw;
            }
        }

        public async Task<AgentHealthStatus> ResetAgent(string handle)
        {
            using var activity = ActivitySource.StartActivity("ResetAgent", ActivityKind.Internal);
            var principalHandle = this.GetPrimaryKeyString();

            var resolvedHandle = ResolveAgentHandle(handle, principalHandle);

            // ACL check — reset requires Configure permission
            await AuthorizeOrThrow(resolvedHandle, AclPermission.Configure);

            activity?.SetTag("principal.handle", principalHandle);
            activity?.SetTag("agent.handle", resolvedHandle);

            logger.LogInformation("Principal resetting agent - PrincipalHandle: {PrincipalHandle}, Handle: {Handle}",
                principalHandle, resolvedHandle);

            try
            {
                var agentGrain = clusterClient.GetGrain<IAgentGrain>(resolvedHandle);
                var health = await agentGrain.ResetAgent();

                logger.LogInformation("Agent reset completed - PrincipalHandle: {PrincipalHandle}, Handle: {Handle}, State: {State}",
                    principalHandle, resolvedHandle, health.State);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return health;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reset agent - PrincipalHandle: {PrincipalHandle}, Handle: {Handle}",
                    principalHandle, resolvedHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "reset_failed"));
                throw;
            }
        }

        public async Task<bool> UntrackAgent(string handle)
        {
            var principalHandle = this.GetPrimaryKeyString();
            var resolvedHandle = ResolveAgentHandle(handle, principalHandle);

            await AuthorizeOrThrow(resolvedHandle, AclPermission.Configure);

            var removed = _trackedAgents.Remove(resolvedHandle);
            var stateRemoved = _state.State.TrackedAgents.Remove(resolvedHandle);

            if (removed || stateRemoved)
            {
                _state.State.LastModified = DateTime.UtcNow;
                await _state.WriteStateAsync();

                logger.LogInformation("Removed tracked agent - PrincipalHandle: {PrincipalHandle}, Handle: {Handle}",
                    principalHandle, resolvedHandle);
            }
            else
            {
                logger.LogInformation("Tracked agent not present during removal - PrincipalHandle: {PrincipalHandle}, Handle: {Handle}",
                    principalHandle, resolvedHandle);
            }

            return removed || stateRemoved;
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

        public async Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false)
        {
            var agents = _trackedAgents.Values
                .Select(CloneTrackedAgentInfo)
                .ToList();

            if (!activate)
            {
                return agents;
            }

            await Task.WhenAll(agents.Select(PopulateHealthAsync));
            return agents;
        }

        private async Task PopulateHealthAsync(TrackedAgentInfo agent)
        {
            try
            {
                var agentGrain = clusterClient.GetGrain<IAgentGrain>(agent.Handle);
                agent.Health = await agentGrain.GetHealth(HealthDetailLevel.Basic);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to activate tracked agent during health warm-up: {Handle}", agent.Handle);
                agent.Health = new AgentHealthStatus
                {
                    Handle = agent.Handle,
                    State = HealthState.Unhealthy,
                    Timestamp = DateTime.UtcNow,
                    IsConfigured = false,
                    Message = $"Health check failed: {ex.Message}",
                    AgentType = agent.AgentType
                };
            }
        }

        private static TrackedAgentInfo CloneTrackedAgentInfo(TrackedAgentInfo agent)
        {
            return new TrackedAgentInfo
            {
                Handle = agent.Handle,
                AgentType = agent.AgentType
            };
        }

        public Task<bool> IsAgentTracked(string handle)
        {
            var principalHandle = this.GetPrimaryKeyString();
            var fullHandle = ResolveAgentHandle(handle, principalHandle);

            // O(1) dictionary lookup
            return Task.FromResult(_trackedAgents.ContainsKey(fullHandle));
        }

        public async Task<List<AgentInfo>> GetAccessibleSharedAgents()
        {
            var principalHandle = this.GetPrimaryKeyString();
            var allAgents = await _agentService.GetAgentsAsync("active");
            var accessible = new List<AgentInfo>();

            foreach (var agent in allAgents)
            {
                var (targetPrincipalHandle, agentHandle) = HandleUtilities.ParseHandle(agent.Key);

                // Skip own agents (already tracked via GetTrackedAgents)
                if (string.Equals(principalHandle, targetPrincipalHandle, StringComparison.OrdinalIgnoreCase))
                    continue;

                var result = await _aclProvider.EvaluateAsync(principalHandle, targetPrincipalHandle, agentHandle, AclPermission.Message);
                if (result.Allowed)
                    accessible.Add(agent);
            }

            return accessible;
        }

        /// <summary>
        /// Checks ACL permissions for cross-principal access. Own-agent access is always allowed.
        /// </summary>
        private async Task AuthorizeOrThrow(string targetHandle, AclPermission required)
        {
            var principalHandle = this.GetPrimaryKeyString();
            var (targetPrincipalHandle, agentHandle) = HandleUtilities.ParseHandle(targetHandle);

            // Own agents always allowed — short-circuit with zero overhead
            if (string.Equals(principalHandle, targetPrincipalHandle, StringComparison.OrdinalIgnoreCase))
                return;

            var result = await _aclProvider.EvaluateAsync(principalHandle, targetPrincipalHandle, agentHandle, required);
            if (!result.Allowed)
            {
                logger.LogWarning("ACL denied: '{PrincipalHandle}' cannot {Permission} on '{TargetHandle}'. {Reason}",
                    principalHandle, required, targetHandle, result.DeniedReason);
                throw new UnauthorizedAccessException(
                    $"Access denied: '{principalHandle}' cannot {required} on '{targetHandle}'. {result.DeniedReason}");
            }

            logger.LogDebug("ACL granted: '{PrincipalHandle}' can {Permission} on '{TargetHandle}'",
                principalHandle, required, targetHandle);
        }

        /// <summary>
        /// Resolves an agent handle. If it contains ':', uses as-is (already qualified).
        /// Otherwise, prefixes with the principal handle.
        /// </summary>
        private static string ResolveAgentHandle(string? handle, string principalHandle)
        {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("Handle cannot be null or empty", nameof(handle));

            var prefix = HandleUtilities.BuildPrefix(principalHandle);
            return HandleUtilities.EnsurePrefix(handle, prefix);
        }
    }
}
