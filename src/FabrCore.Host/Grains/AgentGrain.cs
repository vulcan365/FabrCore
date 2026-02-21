using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Streaming;
using FabrCore.Host.Streaming;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace FabrCore.Host.Grains
{
    internal class AgentGrain : Grain, IAgentGrain, IFabrCoreAgentHost, IRemindable
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.AgentGrain");
        private static readonly Meter Meter = new("FabrCore.Host.AgentGrain");

        // Metrics
        private static readonly Counter<long> AgentConfiguredCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.configured",
            description: "Number of agents configured");

        private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.messages.processed",
            description: "Number of messages processed");

        private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "fabrcore.agent.message.duration",
            unit: "ms",
            description: "Duration of message processing");

        private static readonly Counter<long> StreamMessagesCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.stream.messages",
            description: "Number of stream messages received");

        private static readonly Counter<long> StreamsCreatedCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.streams.created",
            description: "Number of streams created");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.errors",
            description: "Number of errors encountered");

        private static readonly Counter<long> TimerFiredCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.timer.fired",
            description: "Number of timer callbacks fired");

        private static readonly Counter<long> ReminderFiredCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.reminder.fired",
            description: "Number of reminder callbacks fired");

        protected IFabrCoreAgentProxy? fabrcoreAgentProxy;
        protected AgentConfiguration? agentConfiguration;

        private readonly IClusterClient clusterClient;
        private readonly IServiceProvider serviceProvider;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<AgentGrain> logger;
        private readonly IConfiguration configuration;
        private readonly IFabrCoreRegistry _registry;

        // Message persistence state
        private readonly IPersistentState<AgentGrainState> _messageState;
        private readonly List<FabrCoreChatHistoryProvider> _activeChatHistoryProviders = new();

        // Timer and reminder state
        private readonly Dictionary<string, IDisposable> _timers = new();
        private readonly Dictionary<string, (string MessageType, string? Message)> _timerMessages = new();
        private readonly Dictionary<string, (string MessageType, string? Message)> _reminderMessages = new();

        // Health tracking state
        private DateTime? _configuredAt;
        private long _messagesProcessed;

        public AgentGrain(
            IClusterClient clusterClient,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IConfiguration configuration,
            IFabrCoreRegistry registry,
            [PersistentState("agentMessages", "fabrcoreStorage")]
            IPersistentState<AgentGrainState> messageState)
        {
            this.clusterClient = clusterClient;
            this.serviceProvider = serviceProvider;
            this.loggerFactory = loggerFactory;
            this.configuration = configuration;
            _registry = registry;
            _messageState = messageState;

            logger = loggerFactory.CreateLogger<AgentGrain>();
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);

            logger.LogInformation("AgentGrain activated: {Key}", this.GetPrimaryKeyString());

            // Check for persisted configuration and restore agent if found
            if (_messageState.State.Configuration != null)
            {
                var persistedConfig = _messageState.State.Configuration;
                logger.LogInformation("Restoring agent from persisted configuration: {AgentType} {Handle}",
                    persistedConfig.AgentType, persistedConfig.Handle);

                try
                {
                    agentConfiguration = persistedConfig;

                    var agentType = CreateAgent(persistedConfig.AgentType ?? throw new InvalidOperationException("Persisted AgentType is null"));
                    fabrcoreAgentProxy = (IFabrCoreAgentProxy?)ActivatorUtilities.CreateInstance(serviceProvider, agentType, persistedConfig, this);

                    if (fabrcoreAgentProxy != null)
                    {
                        await fabrcoreAgentProxy.InternalInitialize();
                        _configuredAt = DateTime.UtcNow;

                        await CreateStreams(persistedConfig.Streams ?? new List<string>());
                        await RegisterWithManagementGrain();

                        logger.LogInformation("Agent restored successfully from persisted state: {Handle}", persistedConfig.Handle);
                    }
                    else
                    {
                        logger.LogError("Failed to create agent proxy during restoration for type: {AgentType}", persistedConfig.AgentType);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to restore agent from persisted configuration: {Handle}", persistedConfig.Handle);
                    // Clear the failed configuration to allow fresh configuration
                    agentConfiguration = null;
                    fabrcoreAgentProxy = null;
                }
            }
            // If agent was already configured in-memory, register with management grain
            else if (agentConfiguration != null && fabrcoreAgentProxy != null)
            {
                await RegisterWithManagementGrain();
            }
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.LogInformation("AgentGrain deactivating: {Key}, Reason: {Reason}",
                this.GetPrimaryKeyString(), reason.Description);

            // Flush any pending messages from active stores before deactivation
            await FlushAllChatHistoryProvidersAsync();

            // Flush any pending custom state changes before deactivation
            if (fabrcoreAgentProxy != null && fabrcoreAgentProxy.InternalHasPendingStateChanges)
            {
                try
                {
                    await fabrcoreAgentProxy.InternalFlushStateAsync();
                    logger.LogDebug("Flushed pending custom state changes");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to flush custom state changes");
                }
            }

            // Dispose MCP clients
            if (fabrcoreAgentProxy != null)
            {
                try
                {
                    await fabrcoreAgentProxy.InternalDisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to dispose MCP clients");
                }
            }

            // Notify management grain of deactivation
            if (agentConfiguration != null)
            {
                try
                {
                    var registry = GrainFactory.GetGrain<IAgentManagementGrain>(0);
                    await registry.DeactivateAgent(
                        this.GetPrimaryKeyString(),
                        reason.Description);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to deactivate agent in registry: {Key}",
                        this.GetPrimaryKeyString());
                }
            }

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        private async Task RegisterWithManagementGrain()
        {
            try
            {
                var registry = GrainFactory.GetGrain<IAgentManagementGrain>(0);
                await registry.RegisterAgent(
                    this.GetPrimaryKeyString(),
                    agentConfiguration!.AgentType!,
                    agentConfiguration.Handle!);

                logger.LogInformation("Registered agent with management grain: {Key}",
                    this.GetPrimaryKeyString());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register agent with management grain: {Key}",
                    this.GetPrimaryKeyString());
            }
        }



        //IAgentGrain

        public async Task<AgentHealthStatus> ConfigureAgent(
            AgentConfiguration config,
            bool forceReconfigure = false,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            using var activity = ActivitySource.StartActivity("ConfigureAgent", ActivityKind.Internal);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);
            activity?.SetTag("streams.count", config.Streams?.Count ?? 0);
            activity?.SetTag("force.reconfigure", forceReconfigure);

            // If already configured and not forcing reconfigure, return current health
            if (agentConfiguration != null && fabrcoreAgentProxy != null && !forceReconfigure)
            {
                logger.LogInformation("Agent already configured, returning health status: {Handle}",
                    this.GetPrimaryKeyString());
                activity?.SetTag("already.configured", true);
                return await GetHealth(detailLevel);
            }

            logger.LogInformation("Configuring agent: {AgentType} {Handle}", config.AgentType, config.Handle);
            logger.LogDebug("Agent configuration details - Streams: {StreamCount}", config.Streams?.Count ?? 0);

            try
            {
                agentConfiguration = config;

                var agentType = CreateAgent(config.AgentType ?? throw new ArgumentException("AgentType cannot be null", nameof(config)));
                fabrcoreAgentProxy = (IFabrCoreAgentProxy?)ActivatorUtilities.CreateInstance(serviceProvider, agentType, config, this);

                if (fabrcoreAgentProxy == null)
                {
                    logger.LogError("Failed to create agent proxy for type: {AgentType}", config.AgentType);
                    activity?.SetStatus(ActivityStatusCode.Error, "Agent proxy type not found");
                    ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "proxy_creation_failed"),
                        new KeyValuePair<string, object?>("agent.type", config.AgentType));
                    throw new Exception("Agent proxy type not found");
                }

                logger.LogDebug("Agent proxy created successfully for: {AgentType}", config.AgentType);

                await fabrcoreAgentProxy.InternalInitialize();
                _configuredAt = DateTime.UtcNow;
                logger.LogDebug("Agent proxy initialized for: {Handle}", config.Handle);

                await CreateStreams(config.Streams ?? new List<string>());

                // Register with management grain after successful initialization
                await RegisterWithManagementGrain();

                logger.LogInformation("Agent configuration completed successfully: {Handle}", config.Handle);

                // Persist configuration to grain state
                _messageState.State.Configuration = config;
                _messageState.State.LastModified = DateTime.UtcNow;
                await _messageState.WriteStateAsync();
                logger.LogDebug("Configuration persisted for agent: {Handle}", config.Handle);

                AgentConfiguredCounter.Add(1,
                    new KeyValuePair<string, object?>("agent.type", config.AgentType),
                    new KeyValuePair<string, object?>("agent.handle", config.Handle));

                activity?.SetStatus(ActivityStatusCode.Ok);

                // Return health status after configuration
                return await GetHealth(detailLevel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure agent: {AgentType} {Handle}", config.AgentType, config.Handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "configuration_failed"),
                    new KeyValuePair<string, object?>("agent.type", config.AgentType));
                throw;
            }
        }

        public async Task<AgentHealthStatus> GetHealth(HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            using var activity = ActivitySource.StartActivity("GetHealth", ActivityKind.Internal);
            activity?.SetTag("detail.level", detailLevel.ToString());

            var handle = this.GetPrimaryKeyString();
            var isConfigured = agentConfiguration != null && fabrcoreAgentProxy != null;

            // Build basic status
            var status = new AgentHealthStatus
            {
                Handle = handle,
                State = isConfigured ? HealthState.Healthy : HealthState.NotConfigured,
                Timestamp = DateTime.UtcNow,
                IsConfigured = isConfigured,
                Message = isConfigured ? "Agent is healthy" : "Agent not configured"
            };

            if (!isConfigured)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                return status;
            }

            // Add detailed info if requested
            if (detailLevel >= HealthDetailLevel.Detailed)
            {
                status = status with
                {
                    AgentType = agentConfiguration!.AgentType,
                    Uptime = _configuredAt.HasValue ? DateTime.UtcNow - _configuredAt.Value : null,
                    MessagesProcessed = _messagesProcessed,
                    ActiveTimerCount = _timers.Count,
                    ActiveReminderCount = _reminderMessages.Count,
                    StreamCount = agentConfiguration.Streams?.Count ?? 0,
                    Configuration = agentConfiguration
                };
            }

            // Add full diagnostics if requested
            if (detailLevel == HealthDetailLevel.Full)
            {
                var proxyHealth = await fabrcoreAgentProxy!.InternalGetHealth(detailLevel);

                status = status with
                {
                    ProxyHealth = proxyHealth,
                    ActiveStreams = agentConfiguration!.Streams,
                    Diagnostics = new Dictionary<string, string>
                    {
                        ["GrainId"] = this.GetGrainId().ToString(),
                        ["Models"] = agentConfiguration.Models ?? "not set"
                    },
                    State = CombineHealthStates(status.State, proxyHealth.State)
                };
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return status;
        }

        private static HealthState CombineHealthStates(HealthState a, HealthState b)
        {
            return (HealthState)Math.Max((int)a, (int)b);
        }

        // IAgentGrain - Message Thread Methods

        public Task<List<StoredChatMessage>> GetThreadMessages(string threadId)
        {
            if (_messageState.State.MessageThreads.TryGetValue(threadId, out var messages))
            {
                return Task.FromResult(messages.ToList());
            }
            return Task.FromResult(new List<StoredChatMessage>());
        }

        public async Task AddThreadMessages(string threadId, IEnumerable<StoredChatMessage> messages)
        {
            if (!_messageState.State.MessageThreads.ContainsKey(threadId))
            {
                _messageState.State.MessageThreads[threadId] = new List<StoredChatMessage>();
            }

            _messageState.State.MessageThreads[threadId].AddRange(messages);
            _messageState.State.LastModified = DateTime.UtcNow;
            await _messageState.WriteStateAsync();

            logger.LogDebug("Added {Count} messages to thread: {ThreadId}", messages.Count(), threadId);
        }

        public async Task ClearThreadMessages(string threadId)
        {
            if (_messageState.State.MessageThreads.Remove(threadId))
            {
                _messageState.State.LastModified = DateTime.UtcNow;
                await _messageState.WriteStateAsync();
                logger.LogDebug("Cleared messages for thread: {ThreadId}", threadId);
            }
        }

        public async Task ReplaceThreadMessages(string threadId, IEnumerable<StoredChatMessage> messages)
        {
            _messageState.State.MessageThreads[threadId] = messages.ToList();
            _messageState.State.LastModified = DateTime.UtcNow;
            await _messageState.WriteStateAsync();

            logger.LogDebug("Replaced messages for thread: {ThreadId}, new count: {Count}",
                threadId, _messageState.State.MessageThreads[threadId].Count);
        }

        public Task<Dictionary<string, JsonElement>> GetCustomStateAsync()
        {
            return Task.FromResult(_messageState.State.CustomState ?? new Dictionary<string, JsonElement>());
        }

        public async Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
        {
            _messageState.State.CustomState ??= new Dictionary<string, JsonElement>();

            // Apply deletions
            foreach (var key in deletes)
            {
                _messageState.State.CustomState.Remove(key);
            }

            // Apply changes
            foreach (var (key, value) in changes)
            {
                _messageState.State.CustomState[key] = value;
            }

            _messageState.State.LastModified = DateTime.UtcNow;
            await _messageState.WriteStateAsync();

            logger.LogDebug("Merged custom state: {ChangesCount} changes, {DeletesCount} deletes",
                changes.Count, deletes.Count());
        }

        public async Task<AgentMessage> OnMessage(AgentMessage request)
        {
            using var activity = ActivitySource.StartActivity("OnMessage", ActivityKind.Server);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);
            activity?.SetTag("message.kind", request.Kind.ToString());
            activity?.SetTag("to.type", "Agent");  // Receiving entity is always an agent

            logger.LogTrace("OnMessage received - From: {FromHandle}, To: {ToHandle}, Kind: {Kind}",
                request.FromHandle, request.ToHandle, request.Kind);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                if (fabrcoreAgentProxy == null)
                {
                    throw new InvalidOperationException("Agent has not been configured. Call ConfigureAgent first.");
                }

                var response = await fabrcoreAgentProxy.InternalOnMessage(request);
                _messagesProcessed++;
                logger.LogTrace("OnMessage completed - Response ToHandle: {ToHandle}", response?.ToHandle);

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                MessageProcessingDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("message.from", request.FromHandle),
                    new KeyValuePair<string, object?>("message.to", request.ToHandle),
                    new KeyValuePair<string, object?>("message.kind", request.Kind.ToString()));

                MessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("message.from", request.FromHandle),
                    new KeyValuePair<string, object?>("message.to", request.ToHandle),
                    new KeyValuePair<string, object?>("message.kind", request.Kind.ToString()));

                activity?.SetStatus(ActivityStatusCode.Ok);
                return response ?? throw new InvalidOperationException("Agent proxy returned null response");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing message from {FromHandle} to {ToHandle}",
                    request.FromHandle, request.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "message_processing_failed"),
                    new KeyValuePair<string, object?>("message.from", request.FromHandle));
                throw;
            }
            finally
            {
                // Auto-flush all tracked message stores after each message
                await FlushAllChatHistoryProvidersAsync();
            }
        }

        /// <summary>
        /// Flushes all pending messages from tracked chat history providers to persistent storage.
        /// </summary>
        private async Task FlushAllChatHistoryProvidersAsync()
        {
            foreach (var provider in _activeChatHistoryProviders.ToList())
            {
                if (provider.HasPendingMessages)
                {
                    const int maxRetries = 3;
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        try
                        {
                            await provider.FlushAsync();
                            logger.LogDebug("Auto-flushed messages for thread: {ThreadId}", provider.ThreadId);
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == maxRetries)
                            {
                                logger.LogError(ex, "Failed to flush messages after {Attempts} attempts for thread: {ThreadId}",
                                    maxRetries, provider.ThreadId);
                            }
                            else
                            {
                                logger.LogWarning(ex, "Retry {Attempt}/{Max} - flush failed for thread: {ThreadId}",
                                    attempt, maxRetries, provider.ThreadId);
                                await Task.Delay(100 * attempt); // Brief backoff
                            }
                        }
                    }
                }
            }
        }

        private Type CreateAgent(string agentType)
        {
            using var activity = ActivitySource.StartActivity("CreateAgent", ActivityKind.Internal);
            activity?.SetTag("agent.type.alias", agentType);

            logger.LogDebug("Searching for agent type with alias: {AgentType}", agentType);

            if (string.IsNullOrWhiteSpace(agentType))
            {
                logger.LogError("AgentType cannot be null or empty");
                activity?.SetStatus(ActivityStatusCode.Error, "AgentType cannot be null or empty");
                throw new ArgumentException("AgentType cannot be null or empty", nameof(agentType));
            }

            var type = _registry.FindAgentType(agentType);
            if (type == null)
            {
                logger.LogError("No agent type found with alias: {AgentType}", agentType);
                activity?.SetStatus(ActivityStatusCode.Error, $"No agent type found with alias '{agentType}'");
                throw new InvalidOperationException($"No agent type found with alias '{agentType}'");
            }

            logger.LogInformation("Found agent type: {TypeName} for alias: {AgentType}", type.FullName, agentType);
            activity?.SetTag("agent.type.full_name", type.FullName);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return type;
        }



        // IFabrCoreAgentHost

        public string GetHandle()
        {
            var handle = this.GetPrimaryKeyString();
            logger.LogTrace("GetHandle called, returning: {Handle}", handle);
            return handle;
        }

        public async Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
        {
            var targetHandle = handle ?? this.GetPrimaryKeyString();

            using var activity = ActivitySource.StartActivity("GetAgentHealth", ActivityKind.Client);
            activity?.SetTag("target.handle", targetHandle);
            activity?.SetTag("target.is_self", handle == null);
            activity?.SetTag("detail.level", detailLevel.ToString());

            logger.LogDebug("GetAgentHealth - Target: {Handle}, DetailLevel: {DetailLevel}", targetHandle, detailLevel);

            try
            {
                // If querying self, call directly to avoid grain-to-self call
                if (handle == null)
                {
                    var health = await GetHealth(detailLevel);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return health;
                }

                var agentGrain = clusterClient.GetGrain<IAgentGrain>(targetHandle);
                var remoteHealth = await agentGrain.GetHealth(detailLevel);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return remoteHealth;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting health for agent: {Handle}", targetHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            // Ensure FromHandle is set to this agent's handle if not provided
            if (string.IsNullOrEmpty(request.FromHandle))
            {
                request.FromHandle = this.GetPrimaryKeyString();
            }

            using var activity = ActivitySource.StartActivity("SendAndReceiveMessage", ActivityKind.Client);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);
            activity?.SetTag("from.type", "Agent");  // Agent-to-agent communication
            activity?.SetTag("to.type", "Agent");

            logger.LogDebug("SendAndReceiveMessage - From: {FromHandle}, To: {ToHandle}",
                request.FromHandle, request.ToHandle);

            try
            {
                var agentProxy = clusterClient.GetGrain<IAgentGrain>(request.ToHandle ?? throw new ArgumentException("ToHandle cannot be null", nameof(request)));
                var response = await agentProxy.OnMessage(request);
                logger.LogDebug("SendAndReceiveMessage completed - Response received from: {ToHandle}",
                    request.ToHandle);

                activity?.SetStatus(ActivityStatusCode.Ok);
                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in SendAndReceiveMessage from {FromHandle} to {ToHandle}",
                    request.FromHandle, request.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "send_receive_failed"),
                    new KeyValuePair<string, object?>("message.to", request.ToHandle));
                throw;
            }
        }

        public async Task SendMessage(AgentMessage request)
        {
            // Ensure FromHandle is set to this agent's handle if not provided
            if (string.IsNullOrEmpty(request.FromHandle))
            {
                request.FromHandle = this.GetPrimaryKeyString();
            }

            using var activity = ActivitySource.StartActivity("SendMessage", ActivityKind.Producer);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);
            activity?.SetTag("from.type", "Agent");  // Sending agent
            activity?.SetTag("stream.provider", StreamConstants.ProviderName);
            activity?.SetTag("stream.namespace", StreamConstants.AgentChatNamespace);

            logger.LogTrace("SendMessage to stream - From: {FromHandle}, To: {ToHandle}",
                request.FromHandle, request.ToHandle);

            try
            {
                var stream = clusterClient.GetAgentChatStream(request.ToHandle ?? throw new ArgumentException("ToHandle cannot be null", nameof(request)));
                await stream.OnNextAsync(request);
                logger.LogTrace("Message sent to stream for: {ToHandle}", request.ToHandle);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending message to stream for: {ToHandle}", request.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_send_failed"),
                    new KeyValuePair<string, object?>("message.to", request.ToHandle));
                throw;
            }
        }

        public async Task SendEvent(AgentMessage request, string? streamName = null)
        {
            // Ensure FromHandle is set to this agent's handle if not provided
            if (string.IsNullOrEmpty(request.FromHandle))
            {
                request.FromHandle = this.GetPrimaryKeyString();
            }

            using var activity = ActivitySource.StartActivity("SendEvent", ActivityKind.Producer);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("from.type", "Agent");  // Sending agent
            activity?.SetTag("stream.provider", StreamConstants.ProviderName);
            activity?.SetTag("stream.namespace", StreamConstants.AgentEventNamespace);

            try
            {
                if (streamName != null)
                {
                    // Named event stream — publish directly
                    activity?.SetTag("stream.name", streamName);

                    logger.LogTrace("SendEvent to named stream - From: {FromHandle}, StreamName: {StreamName}",
                        request.FromHandle, streamName);

                    var stream = clusterClient.GetAgentEventStream(streamName);
                    await stream.OnNextAsync(request);

                    logger.LogTrace("Event sent to named stream: {StreamName}", streamName);
                }
                else
                {
                    // Default agent event stream
                    activity?.SetTag("message.to", request.ToHandle);

                    logger.LogTrace("SendEvent to stream - From: {FromHandle}, To: {ToHandle}",
                        request.FromHandle, request.ToHandle);

                    var stream = clusterClient.GetAgentEventStream(request.ToHandle ?? throw new ArgumentException("ToHandle cannot be null", nameof(request)));
                    await stream.OnNextAsync(request);

                    logger.LogTrace("Event sent to stream for: {ToHandle}", request.ToHandle);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending event to stream - From: {FromHandle}, To: {ToHandle}, StreamName: {StreamName}",
                    request.FromHandle, request.ToHandle, streamName);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_event_send_failed"),
                    new KeyValuePair<string, object?>("message.to", request.ToHandle));
                throw;
            }
        }

        public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
        {
            using var activity = ActivitySource.StartActivity("RegisterTimer", ActivityKind.Internal);
            activity?.SetTag("timer.name", timerName);
            activity?.SetTag("timer.due_time", dueTime.TotalMilliseconds);
            activity?.SetTag("timer.period", period.TotalMilliseconds);

            logger.LogInformation("Registering timer: {TimerName}, DueTime: {DueTime}, Period: {Period}",
                timerName, dueTime, period);

            // Unregister existing timer with same name if exists
            if (_timers.TryGetValue(timerName, out var existingTimer))
            {
                existingTimer.Dispose();
                _timers.Remove(timerName);
                _timerMessages.Remove(timerName);
                logger.LogDebug("Disposed existing timer: {TimerName}", timerName);
            }

            // Store the message info for this timer
            _timerMessages[timerName] = (messageType, message);

            // Register the timer with Orleans
            var timer = this.RegisterGrainTimer(
                async ct => await OnTimerTick(timerName),
                new GrainTimerCreationOptions
                {
                    DueTime = dueTime,
                    Period = period,
                    Interleave = false
                });

            _timers[timerName] = timer;

            logger.LogInformation("Timer registered successfully: {TimerName}", timerName);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public void UnregisterTimer(string timerName)
        {
            using var activity = ActivitySource.StartActivity("UnregisterTimer", ActivityKind.Internal);
            activity?.SetTag("timer.name", timerName);

            logger.LogInformation("Unregistering timer: {TimerName}", timerName);

            if (_timers.TryGetValue(timerName, out var timer))
            {
                timer.Dispose();
                _timers.Remove(timerName);
                _timerMessages.Remove(timerName);
                logger.LogInformation("Timer unregistered successfully: {TimerName}", timerName);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                logger.LogWarning("Timer not found for unregistration: {TimerName}", timerName);
                activity?.SetStatus(ActivityStatusCode.Error, "Timer not found");
            }
        }

        public async Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
        {
            using var activity = ActivitySource.StartActivity("RegisterReminder", ActivityKind.Internal);
            activity?.SetTag("reminder.name", reminderName);
            activity?.SetTag("reminder.due_time", dueTime.TotalMilliseconds);
            activity?.SetTag("reminder.period", period.TotalMilliseconds);

            logger.LogInformation("Registering reminder: {ReminderName}, DueTime: {DueTime}, Period: {Period}",
                reminderName, dueTime, period);

            try
            {
                // Store the message info for this reminder
                _reminderMessages[reminderName] = (messageType, message);

                // Register the reminder with Orleans
                await this.RegisterOrUpdateReminder(reminderName, dueTime, period);

                logger.LogInformation("Reminder registered successfully: {ReminderName}", reminderName);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register reminder: {ReminderName}", reminderName);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "reminder_registration_failed"),
                    new KeyValuePair<string, object?>("reminder.name", reminderName));
                throw;
            }
        }

        public async Task UnregisterReminder(string reminderName)
        {
            using var activity = ActivitySource.StartActivity("UnregisterReminder", ActivityKind.Internal);
            activity?.SetTag("reminder.name", reminderName);

            logger.LogInformation("Unregistering reminder: {ReminderName}", reminderName);

            try
            {
                var reminder = await this.GetReminder(reminderName);
                if (reminder != null)
                {
                    await this.UnregisterReminder(reminder);
                    _reminderMessages.Remove(reminderName);
                    logger.LogInformation("Reminder unregistered successfully: {ReminderName}", reminderName);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
                    logger.LogWarning("Reminder not found for unregistration: {ReminderName}", reminderName);
                    activity?.SetStatus(ActivityStatusCode.Error, "Reminder not found");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to unregister reminder: {ReminderName}", reminderName);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        // IFabrCoreAgentHost - Chat History Provider Support

        public FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId)
        {
            var provider = new FabrCoreChatHistoryProvider(this, threadId);
            _activeChatHistoryProviders.Add(provider);  // Track for deactivation flush
            logger.LogDebug("Created chat history provider for thread: {ThreadId}", threadId);
            return provider;
        }

        public void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider)
        {
            if (!_activeChatHistoryProviders.Contains(provider))
            {
                _activeChatHistoryProviders.Add(provider);
                logger.LogDebug("Tracking chat history provider for thread: {ThreadId}", provider.ThreadId);
            }
        }

        public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId)
            => GetThreadMessages(threadId);

        public Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => AddThreadMessages(threadId, messages);

        public Task ClearThreadAsync(string threadId)
            => ClearThreadMessages(threadId);

        public Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => ReplaceThreadMessages(threadId, messages);

        Task<Dictionary<string, JsonElement>> IFabrCoreAgentHost.GetCustomStateAsync()
            => GetCustomStateAsync();

        Task IFabrCoreAgentHost.MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
            => MergeCustomStateAsync(changes, deletes);

        // IRemindable implementation
        public async Task ReceiveReminder(string reminderName, TickStatus status)
        {
            using var activity = ActivitySource.StartActivity("ReceiveReminder", ActivityKind.Internal);
            activity?.SetTag("reminder.name", reminderName);
            activity?.SetTag("reminder.current_tick", status.CurrentTickTime);
            activity?.SetTag("reminder.period", status.Period.TotalMilliseconds);

            logger.LogDebug("Reminder fired: {ReminderName}, CurrentTick: {CurrentTick}, Period: {Period}",
                reminderName, status.CurrentTickTime, status.Period);

            ReminderFiredCounter.Add(1,
                new KeyValuePair<string, object?>("reminder.name", reminderName),
                new KeyValuePair<string, object?>("agent.handle", this.GetPrimaryKeyString()));

            try
            {
                if (_reminderMessages.TryGetValue(reminderName, out var messageInfo))
                {
                    await SendTimerOrReminderMessage(reminderName, messageInfo.MessageType, messageInfo.Message, isReminder: true);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
                    logger.LogWarning("No message info found for reminder: {ReminderName}", reminderName);
                    activity?.SetStatus(ActivityStatusCode.Error, "No message info found");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing reminder: {ReminderName}", reminderName);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "reminder_processing_failed"),
                    new KeyValuePair<string, object?>("reminder.name", reminderName));
            }
        }

        private async Task OnTimerTick(string timerName)
        {
            using var activity = ActivitySource.StartActivity("OnTimerTick", ActivityKind.Internal);
            activity?.SetTag("timer.name", timerName);

            logger.LogDebug("Timer fired: {TimerName}", timerName);

            TimerFiredCounter.Add(1,
                new KeyValuePair<string, object?>("timer.name", timerName),
                new KeyValuePair<string, object?>("agent.handle", this.GetPrimaryKeyString()));

            try
            {
                if (_timerMessages.TryGetValue(timerName, out var messageInfo))
                {
                    await SendTimerOrReminderMessage(timerName, messageInfo.MessageType, messageInfo.Message, isReminder: false);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
                    logger.LogWarning("No message info found for timer: {TimerName}", timerName);
                    activity?.SetStatus(ActivityStatusCode.Error, "No message info found");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing timer: {TimerName}", timerName);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "timer_processing_failed"),
                    new KeyValuePair<string, object?>("timer.name", timerName));
            }
        }

        private async Task SendTimerOrReminderMessage(string name, string messageType, string? message, bool isReminder)
        {
            if (fabrcoreAgentProxy == null)
            {
                logger.LogWarning("Agent proxy not initialized, cannot process {Type}: {Name}",
                    isReminder ? "reminder" : "timer", name);
                return;
            }

            var handle = this.GetPrimaryKeyString();
            var agentMessage = new AgentMessage
            {
                FromHandle = handle,
                ToHandle = handle,
                MessageType = messageType,
                Message = message,
                Kind = MessageKind.Response,
                Args = new Dictionary<string, string> { ["reminderName"] = name }
            };

            logger.LogTrace("Sending {Type} message to agent - Name: {Name}, MessageType: {MessageType}",
                isReminder ? "reminder" : "timer", name, messageType);

            await fabrcoreAgentProxy.InternalOnMessage(agentMessage);
        }

        // End Interfaces

        private async Task CreateStreams(List<string> configuredStreams)
        {
            using var activity = ActivitySource.StartActivity("CreateStreams", ActivityKind.Internal);
            var handle = this.GetPrimaryKeyString();
            activity?.SetTag("agent.handle", handle);

            logger.LogDebug("Creating streams for agent: {Handle}", handle);

            // Build list of all streams to subscribe to
            var streamNames = new List<StreamName>
            {
                StreamName.ForAgentChat(handle),
                StreamName.ForAgentEvent(handle)
            };

            // Parse and add any configured streams
            foreach (var streamConfig in configuredStreams)
            {
                if (StreamName.TryParse(streamConfig, out var parsedStream))
                {
                    if (!streamNames.Contains(parsedStream))
                    {
                        streamNames.Add(parsedStream);
                    }
                }
                else
                {
                    logger.LogWarning("Invalid stream name format: {StreamName}", streamConfig);
                }
            }

            activity?.SetTag("streams.count", streamNames.Count);
            logger.LogDebug("Total streams to create: {StreamCount}", streamNames.Count);

            try
            {
                foreach (var streamName in streamNames)
                {
                    await SubscribeToStream(streamName, handle);
                }

                logger.LogInformation("All streams created successfully for agent: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating streams for agent: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "stream_creation_failed"),
                    new KeyValuePair<string, object?>("agent.handle", handle));
                throw;
            }
        }

        private async Task SubscribeToStream(StreamName streamName, string handle)
        {
            using var streamActivity = ActivitySource.StartActivity("SubscribeToStream", ActivityKind.Internal);
            streamActivity?.SetTag("stream.name", streamName.ToString());
            streamActivity?.SetTag("stream.namespace", streamName.Namespace);

            logger.LogTrace("Processing stream: {StreamName}", streamName);

            var stream = this.GetStream<AgentMessage>(streamName);
            var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);

            var handles = await stream.GetAllSubscriptionHandles();
            logger.LogTrace("Found {HandleCount} existing subscription handles for stream: {StreamName}",
                handles.Count, streamName);

            foreach (var existingHandle in handles)
            {
                if (existingHandle.StreamId == streamId)
                {
                    await existingHandle.UnsubscribeAsync();
                    logger.LogTrace("Unsubscribed from existing handle for stream: {StreamName}", streamName);
                }
            }

            // Subscribe with the appropriate handler based on namespace
            if (streamName.IsAgentChat)
            {
                await stream.SubscribeAsync(ReceivedChatMessage);
            }
            else if (streamName.IsAgentEvent)
            {
                await stream.SubscribeAsync(ReceivedEventMessage);
            }
            else
            {
                // For custom namespaces, default to chat message handling
                await stream.SubscribeAsync(ReceivedChatMessage);
            }

            logger.LogInformation("Subscribed to stream: {StreamName}", streamName);

            StreamsCreatedCounter.Add(1,
                new KeyValuePair<string, object?>("stream.name", streamName.ToString()),
                new KeyValuePair<string, object?>("stream.namespace", streamName.Namespace),
                new KeyValuePair<string, object?>("agent.handle", handle));

            streamActivity?.SetStatus(ActivityStatusCode.Ok);
        }

        private async Task ReceivedChatMessage(AgentMessage request, StreamSequenceToken? token = null)
        {
            using var activity = ActivitySource.StartActivity("ReceivedChatMessage", ActivityKind.Consumer);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);
            activity?.SetTag("message.kind", request.Kind.ToString());
            activity?.SetTag("stream.namespace", StreamConstants.AgentChatNamespace);
            activity?.SetTag("to.type", "Agent");  // Receiving entity is always an agent
            if (token != null)
            {
                activity?.SetTag("stream.sequence", token.SequenceNumber);
            }

            logger.LogDebug("Received chat message - From: {FromHandle}, To: {ToHandle}, Kind: {Kind}",
                request.FromHandle, request.ToHandle, request.Kind);

            StreamMessagesCounter.Add(1,
                new KeyValuePair<string, object?>("message.from", request.FromHandle),
                new KeyValuePair<string, object?>("message.to", request.ToHandle),
                new KeyValuePair<string, object?>("stream.namespace", StreamConstants.AgentChatNamespace));

            try
            {
                var response = await OnMessage(request);

                if (response != null && request.Kind == MessageKind.Request && response.ToHandle == request.FromHandle)
                {
                    logger.LogDebug("Sending response back to original sender: {FromHandle}", request.FromHandle);
                    await SendMessage(response);
                }
                else
                {
                    logger.LogTrace("No response sent - Response null: {IsNull}, Kind: {Kind}, ToHandle matches: {Matches}",
                        response == null, request.Kind, response?.ToHandle == request.FromHandle);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing chat message from: {FromHandle}", request.FromHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "chat_message_failed"),
                    new KeyValuePair<string, object?>("message.from", request.FromHandle));
            }
        }

        private async Task ReceivedEventMessage(AgentMessage request, StreamSequenceToken? token = null)
        {
            using var activity = ActivitySource.StartActivity("ReceivedEventMessage", ActivityKind.Consumer);
            activity?.SetTag("message.from", request.FromHandle);
            activity?.SetTag("message.to", request.ToHandle);
            activity?.SetTag("message.type", request.MessageType);
            activity?.SetTag("stream.namespace", StreamConstants.AgentEventNamespace);
            activity?.SetTag("to.type", "Agent");  // Receiving entity is always an agent
            if (token != null)
            {
                activity?.SetTag("stream.sequence", token.SequenceNumber);
            }

            logger.LogDebug("Received event message - From: {FromHandle}, To: {ToHandle}, Type: {MessageType}",
                request.FromHandle, request.ToHandle, request.MessageType);

            StreamMessagesCounter.Add(1,
                new KeyValuePair<string, object?>("message.from", request.FromHandle),
                new KeyValuePair<string, object?>("message.to", request.ToHandle),
                new KeyValuePair<string, object?>("stream.namespace", StreamConstants.AgentEventNamespace));

            try
            {
                if (fabrcoreAgentProxy != null)
                {
                    await fabrcoreAgentProxy.InternalOnEvent(request);
                }
                else
                {
                    logger.LogWarning("Agent proxy not initialized, cannot process event from: {FromHandle}", request.FromHandle);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing event message from: {FromHandle}", request.FromHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "event_message_failed"),
                    new KeyValuePair<string, object?>("message.from", request.FromHandle));
            }
        }
    }
}
