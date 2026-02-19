using Fabr.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Fabr.Sdk
{

    public interface IFabrAgentProxy
    {
        internal Task InternalInitialize();
        internal Task<AgentMessage> InternalOnMessage(AgentMessage message);
        internal Task InternalOnEvent(AgentMessage message);
        internal Task<ProxyHealthStatus> InternalGetHealth(HealthDetailLevel detailLevel);
        internal Task InternalFlushStateAsync();
        internal bool InternalHasPendingStateChanges { get; }

        Task OnInitialize();
        Task<AgentMessage> OnMessage(AgentMessage message);

        /// <summary>
        /// Called when an event message is received on the AgentEvent stream.
        /// Events are fire-and-forget notifications that don't expect a response.
        /// </summary>
        Task OnEvent(AgentMessage message);

        /// <summary>
        /// Gets the health status for this proxy.
        /// Override in derived classes to add custom metrics.
        /// </summary>
        /// <param name="detailLevel">Level of detail requested.</param>
        /// <returns>Proxy health status.</returns>
        Task<ProxyHealthStatus> GetHealth(HealthDetailLevel detailLevel);
    }


    public abstract class FabrAgentProxy : IFabrAgentProxy
    {
        private static readonly ActivitySource ActivitySource = new("Fabr.Sdk.AgentProxy");
        private static readonly Meter Meter = new("Fabr.Sdk.AgentProxy");

        // Metrics
        private static readonly Counter<long> AgentInitializedCounter = Meter.CreateCounter<long>(
            "fabr.agent.proxy.initialized",
            description: "Number of agent proxies initialized");

        private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabr.agent.proxy.messages.processed",
            description: "Number of messages processed by agent proxy");

        private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "fabr.agent.proxy.message.duration",
            unit: "ms",
            description: "Duration of agent proxy message processing");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabr.agent.proxy.errors",
            description: "Number of errors encountered in agent proxy");

        protected readonly AgentConfiguration config;
        protected readonly IFabrAgentHost fabrAgentHost;
        protected readonly IServiceProvider serviceProvider;
        protected readonly ILoggerFactory loggerFactory;
        protected readonly ILogger<FabrAgentProxy> logger;
        protected readonly IConfiguration configuration;
        protected readonly IFabrChatClientService chatClientService;

        private DateTime? _initializedAt;

        // Compaction plumbing — lazily initialized on first TryCompactAsync() call
        private FabrChatHistoryProvider? _chatHistoryProvider;
        private string? _chatClientConfigName;
        private CompactionService? _compactionService;
        private CompactionConfig? _compactionConfig;
        private bool _compactionInitialized;

        // Custom state persistence
        private Dictionary<string, JsonElement>? _customStateCache;
        private readonly Dictionary<string, JsonElement> _pendingStateChanges = new();
        private readonly HashSet<string> _pendingStateDeletes = new();
        private bool _customStateLoaded;

        public FabrAgentProxy(AgentConfiguration config, IServiceProvider serviceProvider, IFabrAgentHost fabrAgentHost)
        {
            this.config = config;
            this.serviceProvider = serviceProvider;
            this.fabrAgentHost = fabrAgentHost;

            // Resolve dependencies from service provider
            this.loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            this.configuration = serviceProvider.GetRequiredService<IConfiguration>();
            this.chatClientService = serviceProvider.GetRequiredService<IFabrChatClientService>();

            logger = loggerFactory.CreateLogger<FabrAgentProxy>();

            logger.LogDebug("FabrAgentProxy created - AgentType: {AgentType}, Handle: {Handle}",
                config.AgentType, config.Handle);
        }

        protected async Task<Microsoft.Extensions.AI.IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
        {
            return await chatClientService.GetChatClient(name, networkTimeoutSeconds);
        }

#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        protected async Task<Microsoft.Extensions.AI.ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
        {
            return await chatClientService.GetAudioClient(name, networkTimeoutSeconds);
        }
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.


        /// <summary>
        /// Creates a ChatClientAgent with standard Fabr configuration.
        /// Chat messages are automatically persisted to Orleans grain state via FabrChatHistoryProvider.
        /// Use configureOptions to wire AIContextProviderFactory for dynamic context injection.
        /// </summary>
        /// <param name="chatClientConfigName">Name of the chat client configuration (e.g., "OpenAIProd").</param>
        /// <param name="threadId">Unique identifier for the conversation thread. Used for message persistence.</param>
        /// <param name="tools">Optional tools to make available to the agent.</param>
        /// <param name="configureOptions">Optional action to further configure ChatClientAgentOptions (e.g., AIContextProviderFactory).</param>
        /// <returns>A ChatClientAgentResult containing the configured agent and its session.</returns>
        protected async Task<ChatClientAgentResult> CreateChatClientAgent(
            string chatClientConfigName,
            string threadId,
            IList<AITool>? tools = null,
            Action<ChatClientAgentOptions>? configureOptions = null)
        {
            var chatClient = await GetChatClient(chatClientConfigName);

            // Capture the provider reference so callers can use it for compaction
            FabrChatHistoryProvider? capturedProvider = null;
            var originalFactory = FabrChatHistoryProvider.CreateFactory(fabrAgentHost, threadId, logger);

            var options = new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = config.SystemPrompt,
                    Tools = tools
                },
                Name = fabrAgentHost.GetHandle(),
                // Wire up FabrChatHistoryProvider for automatic message persistence
                ChatHistoryProviderFactory = async (ctx, ct) =>
                {
                    var provider = await originalFactory(ctx, ct);
                    capturedProvider = provider as FabrChatHistoryProvider;
                    return provider;
                }
            };

            // Allow caller to configure options (including AIContextProviderFactory)
            configureOptions?.Invoke(options);

            var agent = new ChatClientAgent(chatClient, options)
                .AsBuilder()
                .UseOpenTelemetry(null, cfg => cfg.EnableSensitiveData = true)
                .Build(serviceProvider);

            var session = await agent.GetNewSessionAsync();

            // Auto-store for compaction support
            _chatHistoryProvider = capturedProvider;
            _chatClientConfigName = chatClientConfigName;

            logger.LogDebug("Created ChatClientAgent - Config: {Config}, ThreadId: {ThreadId}",
                chatClientConfigName, threadId);

            return new ChatClientAgentResult(agent, session, capturedProvider);
        }

#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        protected async Task<ISpeechToTextClient> CreateAudioClientAgent(
            string chatClientConfigName,
            string threadId,
            IList<AITool>? tools = null,
            Action<ChatClientAgentOptions>? configureOptions = null)
        {
            var audioClient = await GetAudioClient(chatClientConfigName);
            return audioClient;
        }
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.


        protected async Task<List<AITool>> ResolveConfiguredToolsAsync()
        {
            var registry = serviceProvider.GetRequiredService<FabrToolRegistry>();
            return await registry.ResolveToolsAsync(serviceProvider, config.Plugins, config.Tools, config, fabrAgentHost);
        }

        #region Compaction

        /// <summary>
        /// Attempts to compact the chat history if the token count exceeds the configured threshold.
        /// Safe to call on every message — returns null if compaction is not configured or not needed.
        /// On first call, lazily resolves CompactionService from DI and builds CompactionConfig from agent args.
        /// </summary>
        /// <returns>The compaction result if compaction ran, or null if skipped/not configured.</returns>
        protected async Task<CompactionResult?> TryCompactAsync(Func<Task>? onCompacting = null)
        {
            if (_chatHistoryProvider is null || _chatClientConfigName is null)
                return null;

            try
            {
                if (!_compactionInitialized)
                {
                    _compactionService = serviceProvider.GetService<CompactionService>();
                    if (_compactionService is not null)
                    {
                        _compactionConfig = await BuildCompactionConfigAsync();
                    }
                    _compactionInitialized = true;
                }

                if (_compactionService is null || _compactionConfig is null)
                    return null;

                var result = await _compactionService.CompactIfNeededAsync(
                    _chatHistoryProvider, _compactionConfig, _chatClientConfigName, onCompacting);

                if (result.WasCompacted)
                {
                    logger.LogInformation(
                        "Compacted history for '{Handle}': {Before} → {After} messages (~{TokensBefore} → ~{TokensAfter} tokens)",
                        config.Handle,
                        result.OriginalMessageCount, result.CompactedMessageCount,
                        result.EstimatedTokensBefore, result.EstimatedTokensAfter);
                }

                return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Compaction failed for '{Handle}' — continuing without compaction", config.Handle);
                return null;
            }
        }

        private async Task<CompactionConfig> BuildCompactionConfigAsync()
        {
            var args = config.Args ?? new Dictionary<string, string>();

            var enabled = !args.TryGetValue("CompactionEnabled", out var enabledStr)
                || !bool.TryParse(enabledStr, out var enabledVal)
                || enabledVal;

            var keepLastN = args.TryGetValue("CompactionKeepLastN", out var keepStr)
                && int.TryParse(keepStr, out var keepVal) ? keepVal : 20;

            int? maxContextTokens = args.TryGetValue("CompactionMaxContextTokens", out var maxStr)
                && int.TryParse(maxStr, out var maxVal) ? maxVal : null;

            // Fall back to model configuration's ContextWindowTokens
            if (maxContextTokens is null)
            {
                try
                {
                    var modelConfig = await chatClientService.GetModelConfigurationAsync(_chatClientConfigName!);
                    maxContextTokens = modelConfig.ContextWindowTokens;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not load model configuration for compaction context window fallback");
                }
            }

            var threshold = args.TryGetValue("CompactionThreshold", out var threshStr)
                && double.TryParse(threshStr, System.Globalization.CultureInfo.InvariantCulture, out var threshVal)
                ? threshVal : 0.75;

            return new CompactionConfig
            {
                Enabled = enabled,
                KeepLastN = keepLastN,
                MaxContextTokens = maxContextTokens,
                Threshold = threshold
            };
        }

        #endregion

        #region Custom State API

        /// <summary>
        /// Ensures custom state is loaded from persistent storage.
        /// Called lazily on first state access.
        /// </summary>
        private async Task EnsureStateLoadedAsync()
        {
            if (!_customStateLoaded)
            {
                _customStateCache = await fabrAgentHost.GetCustomStateAsync();
                _customStateLoaded = true;
                logger.LogDebug("Loaded custom state with {Count} keys", _customStateCache.Count);
            }
        }

        /// <summary>
        /// Gets a strongly-typed state value by key.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the state to.</typeparam>
        /// <param name="key">The state key.</param>
        /// <returns>The deserialized value, or default if not found.</returns>
        protected async Task<T?> GetStateAsync<T>(string key)
        {
            await EnsureStateLoadedAsync();

            // Check pending changes first
            if (_pendingStateChanges.TryGetValue(key, out var pendingElement))
            {
                return pendingElement.Deserialize<T>();
            }

            // Check if deleted
            if (_pendingStateDeletes.Contains(key))
            {
                return default;
            }

            // Check cache
            if (_customStateCache != null && _customStateCache.TryGetValue(key, out var element))
            {
                return element.Deserialize<T>();
            }

            return default;
        }

        /// <summary>
        /// Gets a strongly-typed state value, creating it with a factory if not found.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the state to.</typeparam>
        /// <param name="key">The state key.</param>
        /// <param name="factory">Factory function to create the value if not found.</param>
        /// <returns>The existing or newly created value.</returns>
        protected async Task<T> GetStateOrCreateAsync<T>(string key, Func<T> factory)
        {
            var existing = await GetStateAsync<T>(key);
            if (existing != null)
            {
                return existing;
            }

            var created = factory();
            SetState(key, created);
            return created;
        }

        /// <summary>
        /// Checks if a state key exists.
        /// </summary>
        /// <param name="key">The state key.</param>
        /// <returns>True if the key exists and hasn't been deleted.</returns>
        protected async Task<bool> HasStateAsync(string key)
        {
            await EnsureStateLoadedAsync();

            if (_pendingStateDeletes.Contains(key))
            {
                return false;
            }

            if (_pendingStateChanges.ContainsKey(key))
            {
                return true;
            }

            return _customStateCache?.ContainsKey(key) ?? false;
        }

        /// <summary>
        /// Sets a strongly-typed state value. Changes are buffered until FlushStateAsync is called.
        /// </summary>
        /// <typeparam name="T">The type of the value.</typeparam>
        /// <param name="key">The state key.</param>
        /// <param name="value">The value to store.</param>
        protected void SetState<T>(string key, T value)
        {
            var element = JsonSerializer.SerializeToElement(value);
            _pendingStateChanges[key] = element;
            _pendingStateDeletes.Remove(key);
            logger.LogTrace("Set state key: {Key}", key);
        }

        /// <summary>
        /// Removes a state key. Changes are buffered until FlushStateAsync is called.
        /// </summary>
        /// <param name="key">The state key to remove.</param>
        protected void RemoveState(string key)
        {
            _pendingStateChanges.Remove(key);
            _pendingStateDeletes.Add(key);
            logger.LogTrace("Removed state key: {Key}", key);
        }

        /// <summary>
        /// Returns true if there are unsaved state changes.
        /// </summary>
        protected bool HasPendingStateChanges => _pendingStateChanges.Count > 0 || _pendingStateDeletes.Count > 0;

        /// <summary>
        /// Persists all pending state changes to Orleans grain storage.
        /// </summary>
        protected async Task FlushStateAsync()
        {
            if (!HasPendingStateChanges)
            {
                return;
            }

            await fabrAgentHost.MergeCustomStateAsync(_pendingStateChanges, _pendingStateDeletes);

            // Update local cache
            if (_customStateCache != null)
            {
                foreach (var key in _pendingStateDeletes)
                {
                    _customStateCache.Remove(key);
                }
                foreach (var (key, value) in _pendingStateChanges)
                {
                    _customStateCache[key] = value;
                }
            }

            logger.LogDebug("Flushed state: {ChangesCount} changes, {DeletesCount} deletes",
                _pendingStateChanges.Count, _pendingStateDeletes.Count);

            _pendingStateChanges.Clear();
            _pendingStateDeletes.Clear();
        }

        // Internal methods for IFabrAgentProxy
        bool IFabrAgentProxy.InternalHasPendingStateChanges => HasPendingStateChanges;

        async Task IFabrAgentProxy.InternalFlushStateAsync()
        {
            await FlushStateAsync();
        }

        #endregion

        public abstract Task<AgentMessage> OnMessage(AgentMessage message);
        public abstract Task OnInitialize();

        /// <summary>
        /// Called when an event message is received on the AgentEvent stream.
        /// Override this method to handle events separately from chat messages.
        /// Default implementation logs and ignores the event.
        /// </summary>
        public virtual Task OnEvent(AgentMessage message)
        {
            logger.LogDebug("Event received but not handled - From: {FromHandle}, Type: {MessageType}",
                message.FromHandle, message.MessageType);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the health status for this proxy.
        /// Override in derived classes to add custom metrics.
        /// </summary>
        /// <param name="detailLevel">Level of detail requested.</param>
        /// <returns>Proxy health status.</returns>
        public virtual Task<ProxyHealthStatus> GetHealth(HealthDetailLevel detailLevel)
        {
            return Task.FromResult(new ProxyHealthStatus
            {
                State = HealthState.Healthy,
                IsInitialized = _initializedAt.HasValue,
                ProxyTypeName = GetType().Name,
                InitializedAt = _initializedAt,
                CustomMetrics = GetCustomHealthMetrics(detailLevel),
                Message = "Proxy is healthy"
            });
        }

        /// <summary>
        /// Override to add custom metrics to health status.
        /// </summary>
        /// <param name="detailLevel">Level of detail requested.</param>
        /// <returns>Custom metrics dictionary or null.</returns>
        protected virtual Dictionary<string, string>? GetCustomHealthMetrics(HealthDetailLevel detailLevel)
        {
            return null;
        }

        async Task IFabrAgentProxy.InternalInitialize()
        {
            using var activity = ActivitySource.StartActivity("InternalInitialize", ActivityKind.Internal);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);

            logger.LogInformation("Initializing agent proxy - AgentType: {AgentType}, Handle: {Handle}",
                config.AgentType, config.Handle);

            try
            {
                await OnInitialize();

                _initializedAt = DateTime.UtcNow;

                AgentInitializedCounter.Add(1,
                    new KeyValuePair<string, object?>("agent.type", config.AgentType),
                    new KeyValuePair<string, object?>("agent.handle", config.Handle));

                logger.LogInformation("Agent proxy initialized successfully - Handle: {Handle}", config.Handle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize agent proxy - AgentType: {AgentType}, Handle: {Handle}",
                    config.AgentType, config.Handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "initialization_failed"),
                    new KeyValuePair<string, object?>("agent.type", config.AgentType));
                throw;
            }
        }

        async Task<ProxyHealthStatus> IFabrAgentProxy.InternalGetHealth(HealthDetailLevel detailLevel)
        {
            using var activity = ActivitySource.StartActivity("InternalGetHealth", ActivityKind.Internal);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);
            activity?.SetTag("detail.level", detailLevel.ToString());

            logger.LogTrace("Getting proxy health status - Handle: {Handle}, DetailLevel: {DetailLevel}",
                config.Handle, detailLevel);

            try
            {
                var health = await GetHealth(detailLevel);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return health;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting proxy health - Handle: {Handle}", config.Handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);

                return new ProxyHealthStatus
                {
                    State = HealthState.Unhealthy,
                    IsInitialized = _initializedAt.HasValue,
                    ProxyTypeName = GetType().Name,
                    InitializedAt = _initializedAt,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        async Task<AgentMessage> IFabrAgentProxy.InternalOnMessage(AgentMessage message)
        {
            using var activity = ActivitySource.StartActivity("InternalOnMessage", ActivityKind.Server);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);
            activity?.SetTag("message.kind", message.Kind.ToString());

            logger.LogTrace("Agent proxy processing message - From: {FromHandle}, To: {ToHandle}",
                message.FromHandle, message.ToHandle);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await OnMessage(message);

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                MessageProcessingDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("agent.type", config.AgentType),
                    new KeyValuePair<string, object?>("agent.handle", config.Handle),
                    new KeyValuePair<string, object?>("message.from", message.FromHandle));

                MessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("agent.type", config.AgentType),
                    new KeyValuePair<string, object?>("agent.handle", config.Handle),
                    new KeyValuePair<string, object?>("message.kind", message.Kind.ToString()));

                logger.LogTrace("Agent proxy message processed successfully - Duration: {Duration}ms", elapsed);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing message in agent proxy - From: {FromHandle}, To: {ToHandle}",
                    message.FromHandle, message.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "message_processing_failed"),
                    new KeyValuePair<string, object?>("agent.type", config.AgentType));
                throw;
            }
        }

        async Task IFabrAgentProxy.InternalOnEvent(AgentMessage message)
        {
            using var activity = ActivitySource.StartActivity("InternalOnEvent", ActivityKind.Server);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.type", message.MessageType);

            logger.LogTrace("Agent proxy processing event - From: {FromHandle}, Type: {MessageType}",
                message.FromHandle, message.MessageType);

            try
            {
                await OnEvent(message);

                MessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("agent.type", config.AgentType),
                    new KeyValuePair<string, object?>("agent.handle", config.Handle),
                    new KeyValuePair<string, object?>("message.type", "event"));

                logger.LogTrace("Agent proxy event processed successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing event in agent proxy - From: {FromHandle}, Type: {MessageType}",
                    message.FromHandle, message.MessageType);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "event_processing_failed"),
                    new KeyValuePair<string, object?>("agent.type", config.AgentType));
                throw;
            }
        }
    }
}
