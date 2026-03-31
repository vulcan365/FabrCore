using FabrCore.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenAI.Audio;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace FabrCore.Sdk
{

    public interface IFabrCoreAgentProxy
    {
        internal Task InternalInitialize();
        internal Task<AgentMessage> InternalOnMessage(AgentMessage message);
        internal Task InternalOnEvent(EventMessage message);
        internal Task<ProxyHealthStatus> InternalGetHealth(HealthDetailLevel detailLevel);
        internal Task InternalFlushStateAsync();
        internal Task InternalDisposeAsync();
        internal bool InternalHasPendingStateChanges { get; }

        Task OnInitialize();
        Task<AgentMessage> OnMessage(AgentMessage message);

        /// <summary>
        /// Called when an event message is received on the AgentEvent stream.
        /// Events are fire-and-forget notifications that don't expect a response.
        /// </summary>
        Task OnEvent(EventMessage message);

        /// <summary>
        /// Gets the health status for this proxy.
        /// Override in derived classes to add custom metrics.
        /// </summary>
        /// <param name="detailLevel">Level of detail requested.</param>
        /// <returns>Proxy health status.</returns>
        Task<ProxyHealthStatus> GetHealth(HealthDetailLevel detailLevel);

        /// <summary>
        /// Called when compaction is triggered (token threshold exceeded).
        /// Override to implement custom compaction logic. The default implementation
        /// uses CompactionService to summarize old messages via LLM.
        /// </summary>
        /// <param name="chatHistoryProvider">The chat history provider containing messages to compact.</param>
        /// <param name="compactionConfig">Compaction configuration (thresholds, keep count, etc.).</param>
        /// <returns>The compaction result, or null if compaction was skipped.</returns>
        Task<CompactionResult?> OnCompaction(FabrCoreChatHistoryProvider chatHistoryProvider, CompactionConfig compactionConfig, int estimatedTokens = 0);

        /// <summary>
        /// The current status message for heartbeat display. When set, the grain's heartbeat loop
        /// uses this instead of the default "Thinking.." message. Set to null to revert to default.
        /// </summary>
        string? StatusMessage { get; set; }
    }


    public abstract class FabrCoreAgentProxy : IFabrCoreAgentProxy
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Sdk.AgentProxy");
        private static readonly Meter Meter = new("FabrCore.Sdk.AgentProxy");

        // Metrics
        private static readonly Counter<long> AgentInitializedCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.proxy.initialized",
            description: "Number of agent proxies initialized");

        private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.proxy.messages.processed",
            description: "Number of messages processed by agent proxy");

        private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "fabrcore.agent.proxy.message.duration",
            unit: "ms",
            description: "Duration of agent proxy message processing");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.proxy.errors",
            description: "Number of errors encountered in agent proxy");

        private static readonly Counter<long> McpServersConnectedCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.proxy.mcp.servers.connected",
            description: "Number of MCP servers successfully connected");

        private static readonly Counter<long> McpErrorsCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.proxy.mcp.errors",
            description: "Number of MCP connection errors");

        private static readonly Counter<long> McpServersDisposedCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.proxy.mcp.servers.disposed",
            description: "Number of MCP servers disposed");

        protected readonly AgentConfiguration config;
        protected readonly IFabrCoreAgentHost fabrcoreAgentHost;
        protected readonly IServiceProvider serviceProvider;
        protected readonly ILoggerFactory loggerFactory;
        protected readonly ILogger<FabrCoreAgentProxy> logger;
        protected readonly IConfiguration configuration;
        protected readonly IFabrCoreChatClientService chatClientService;

        private DateTime? _initializedAt;

        // Tracks the message currently being processed (set by InternalOnMessage)
        private AgentMessage? _activeMessage;

        /// <summary>The message currently being processed. Set automatically by InternalOnMessage.</summary>
        protected AgentMessage? ActiveMessage => _activeMessage;

        private volatile string? _statusMessage;

        /// <summary>
        /// Sets the status message shown in the heartbeat loop.
        /// The grain's _status heartbeat sends this instead of "Thinking..".
        /// Pass null to revert to the default.
        /// </summary>
        protected void SetStatusMessage(string? message) => _statusMessage = message;

        /// <inheritdoc/>
        string? IFabrCoreAgentProxy.StatusMessage
        {
            get => _statusMessage;
            set => _statusMessage = value;
        }

        // MCP client lifecycle tracking
        private readonly List<McpClient> _mcpClients = new();

        // Compaction plumbing — lazily initialized on first TryCompactAsync() call
        private FabrCoreChatHistoryProvider? _chatHistoryProvider;
        private string? _chatClientConfigName;
        private CompactionService? _compactionService;
        private CompactionConfig? _compactionConfig;
        private bool _compactionInitialized;

        /// <summary>The lazily-resolved CompactionService instance, available after the first TryCompactAsync() call.</summary>
        protected CompactionService? CompactionServiceInstance => _compactionService;

        /// <summary>The chat client configuration name used for compaction LLM calls.</summary>
        protected string? CompactionChatClientConfigName => _chatClientConfigName;

        // Custom state persistence
        private Dictionary<string, JsonElement>? _customStateCache;
        private readonly Dictionary<string, JsonElement> _pendingStateChanges = new();
        private readonly HashSet<string> _pendingStateDeletes = new();
        private bool _customStateLoaded;

        public FabrCoreAgentProxy(AgentConfiguration config, IServiceProvider serviceProvider, IFabrCoreAgentHost fabrcoreAgentHost)
        {
            this.config = config;
            this.serviceProvider = serviceProvider;
            this.fabrcoreAgentHost = fabrcoreAgentHost;

            // Resolve dependencies from service provider
            this.loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            this.configuration = serviceProvider.GetRequiredService<IConfiguration>();
            this.chatClientService = serviceProvider.GetRequiredService<IFabrCoreChatClientService>();

            logger = loggerFactory.CreateLogger<FabrCoreAgentProxy>();

            logger.LogDebug("FabrCoreAgentProxy created - AgentType: {AgentType}, Handle: {Handle}",
                config.AgentType, config.Handle);
        }

        protected async Task<Microsoft.Extensions.AI.IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
        {
            var client = await chatClientService.GetChatClient(name, networkTimeoutSeconds);
            return new TokenTrackingChatClient(client);
        }

#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        protected async Task<Microsoft.Extensions.AI.ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
        {
            return await chatClientService.GetAudioClient(name, networkTimeoutSeconds);
        }
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.


        /// <summary>
        /// Creates a ChatClientAgent with standard FabrCore configuration.
        /// Chat messages are automatically persisted to Orleans grain state via FabrCoreChatHistoryProvider.
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

            // Create the history provider for automatic message persistence
            var historyProvider = FabrCoreChatHistoryProvider.Create(fabrcoreAgentHost, threadId, logger);

            var options = new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Instructions = config.SystemPrompt,
                    Tools = tools
                },
                Name = fabrcoreAgentHost.GetHandle(),
                ChatHistoryProvider = historyProvider
            };

            // Allow caller to configure options (including AIContextProviders)
            configureOptions?.Invoke(options);

            var agent = new ChatClientAgent(chatClient, options)
                .AsBuilder()
                .UseOpenTelemetry(null, cfg => cfg.EnableSensitiveData = true)
                .Build(serviceProvider);

            var session = await agent.CreateSessionAsync();

            // Auto-store for compaction support
            _chatHistoryProvider = historyProvider;
            _chatClientConfigName = chatClientConfigName;

            logger.LogDebug("Created ChatClientAgent - Config: {Config}, ThreadId: {ThreadId}",
                chatClientConfigName, threadId);

            return new ChatClientAgentResult(agent, session, historyProvider);
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
            var registry = serviceProvider.GetRequiredService<FabrCoreToolRegistry>();
            var tools = await registry.ResolveToolsAsync(serviceProvider, config.Plugins, config.Tools, config, fabrcoreAgentHost);

            // Connect configured MCP servers (fail-open: log warning and continue on failure)
            if (config.McpServers is { Count: > 0 })
            {
                foreach (var mcpConfig in config.McpServers)
                {
                    try
                    {
                        var mcpTools = await ConnectMcpServerAsync(mcpConfig);
                        tools.AddRange(mcpTools);
                        logger.LogInformation("MCP server '{Name}' provided {ToolCount} tools",
                            mcpConfig.Name ?? "(unnamed)", mcpTools.Count);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to connect MCP server '{Name}' — agent will continue without its tools",
                            mcpConfig.Name ?? "(unnamed)");
                        McpErrorsCounter.Add(1,
                            new KeyValuePair<string, object?>("agent.handle", config.Handle),
                            new KeyValuePair<string, object?>("mcp.server", mcpConfig.Name));
                    }
                }
            }

            logger.LogInformation("Agent '{Handle}' resolved {ToolCount} total tools: [{ToolNames}]",
                config.Handle,
                tools.Count,
                string.Join(", ", tools.Select(t => (t as AIFunction)?.Name ?? t.GetType().Name)));

            return tools;
        }

        /// <summary>
        /// Connects to an MCP server and returns its tools as AITool instances.
        /// The MCP client is tracked for automatic disposal on grain deactivation.
        /// For config-driven MCP (via McpServers), failures are caught by ResolveConfiguredToolsAsync.
        /// For code-driven usage, exceptions propagate to the caller.
        /// </summary>
        /// <param name="mcpConfig">The MCP server configuration.</param>
        /// <returns>List of AI tools provided by the MCP server.</returns>
        protected async Task<IList<AITool>> ConnectMcpServerAsync(McpServerConfig mcpConfig)
        {
            using var activity = ActivitySource.StartActivity("ConnectMcpServerAsync", ActivityKind.Client);
            activity?.SetTag("mcp.server.name", mcpConfig.Name);
            activity?.SetTag("mcp.transport", mcpConfig.TransportType.ToString());

            logger.LogInformation("Connecting to MCP server '{Name}' via {Transport}",
                mcpConfig.Name ?? "(unnamed)", mcpConfig.TransportType);

            IClientTransport transport = mcpConfig.TransportType switch
            {
                McpTransportType.Stdio => new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = mcpConfig.Name,
                    Command = mcpConfig.Command ?? throw new ArgumentException($"MCP server '{mcpConfig.Name}' requires a Command for Stdio transport"),
                    Arguments = mcpConfig.Arguments,
                    EnvironmentVariables = mcpConfig.Env?.Count > 0
                        ? mcpConfig.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
                        : null
                }, loggerFactory),

                McpTransportType.Http => new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = mcpConfig.Name,
                    Endpoint = new Uri(mcpConfig.Url ?? throw new ArgumentException($"MCP server '{mcpConfig.Name}' requires a Url for Http transport")),
                    AdditionalHeaders = mcpConfig.Headers?.Count > 0
                        ? mcpConfig.Headers.ToDictionary(kv => kv.Key, kv => kv.Value)
                        : null
                }, loggerFactory),

                _ => throw new ArgumentException($"Unsupported MCP transport type: {mcpConfig.TransportType}")
            };

            var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory);
            _mcpClients.Add(client);

            var tools = await client.ListToolsAsync();

            McpServersConnectedCounter.Add(1,
                new KeyValuePair<string, object?>("agent.handle", config.Handle),
                new KeyValuePair<string, object?>("mcp.server", mcpConfig.Name));

            logger.LogInformation("Connected to MCP server '{Name}' — {ToolCount} tools available: [{ToolNames}]",
                mcpConfig.Name ?? "(unnamed)",
                tools.Count,
                string.Join(", ", tools.Select(t => t.Name)));

            activity?.SetTag("mcp.tools.count", tools.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return tools.Cast<AITool>().ToList();
        }

        #region Compaction

        /// <summary>
        /// Called when compaction is triggered (token threshold exceeded).
        /// Called only when the token threshold is exceeded and compaction is needed.
        /// Override to implement custom compaction logic (e.g., different prompts, models, or summarization strategies).
        /// The default implementation uses CompactionService to summarize old messages via LLM.
        /// </summary>
        /// <param name="chatHistoryProvider">The chat history provider containing messages to compact.</param>
        /// <param name="compactionConfig">Compaction configuration (thresholds, keep count, etc.).</param>
        /// <param name="estimatedTokens">The estimated token count that triggered compaction.</param>
        /// <returns>The compaction result, or null if compaction was skipped.</returns>
        public virtual async Task<CompactionResult?> OnCompaction(
            FabrCoreChatHistoryProvider chatHistoryProvider,
            CompactionConfig compactionConfig,
            int estimatedTokens = 0)
        {
            if (_compactionService is null || _chatClientConfigName is null)
                return null;

            // Set status so the grain's heartbeat loop shows "Compacting.." instead of "Thinking.."
            _statusMessage = "Compacting..";
            try
            {
                return await _compactionService.CompactIfNeededAsync(
                    chatHistoryProvider, compactionConfig, _chatClientConfigName);
            }
            finally
            {
                _statusMessage = null;
            }
        }

        /// <summary>
        /// Attempts to compact the chat history if the token count exceeds the configured threshold.
        /// Called automatically after each OnMessage. Returns null if compaction is not configured or not needed.
        /// On first call, lazily resolves CompactionService from DI and builds CompactionConfig from agent args.
        /// </summary>
        private async Task<CompactionResult?> TryCompactAsync(Func<Task>? onCompacting = null)
        {
            if (_chatHistoryProvider is null || _chatClientConfigName is null)
                return null;

            try
            {
                if (!_compactionInitialized)
                {
                    _compactionService = serviceProvider.GetService<CompactionService>();
                    _compactionConfig = await BuildCompactionConfigAsync();
                    _compactionInitialized = true;

                    logger.LogDebug(
                        "Compaction initialized for '{Handle}': Enabled={Enabled}, MaxContextTokens={MaxTokens}, Threshold={Threshold}, KeepLastN={KeepLastN}",
                        config.Handle, _compactionConfig.Enabled, _compactionConfig.MaxContextTokens, _compactionConfig.Threshold, _compactionConfig.KeepLastN);
                }

                if (_compactionConfig is null || !_compactionConfig.Enabled)
                    return null;

                if (_compactionConfig.MaxContextTokens <= 0)
                    return null;

                // Check threshold before calling OnCompaction
                if (_chatHistoryProvider.HasPendingMessages)
                    await _chatHistoryProvider.FlushAsync();

                var messages = await _chatHistoryProvider.GetStoredMessagesAsync();
                var estimatedTokens = CompactionService.EstimateTokens(messages);
                var threshold = (int)(_compactionConfig.MaxContextTokens * _compactionConfig.Threshold);

                if (estimatedTokens <= threshold)
                {
                    logger.LogDebug(
                        "Compaction not needed for '{Handle}': ~{EstimatedTokens} estimated tokens <= {Threshold} threshold ({MessageCount} messages)",
                        config.Handle, estimatedTokens, threshold, messages.Count);
                    return null;
                }

                logger.LogInformation(
                    "Compaction needed for '{Handle}': ~{EstimatedTokens} estimated tokens exceeds {Threshold} threshold ({Ratio:P0} of {Max})",
                    config.Handle, estimatedTokens, threshold, _compactionConfig.Threshold, _compactionConfig.MaxContextTokens);

                if (onCompacting is not null)
                    await onCompacting();

                // Delegate to OnCompaction — only called when threshold is exceeded
                var result = await OnCompaction(_chatHistoryProvider, _compactionConfig, estimatedTokens);

                if (result?.WasCompacted == true)
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

            // Defaults
            var enabled = true;
            var maxContextTokens = 25000;
            var keepLastN = 20;
            var threshold = 0.75;

            // Layer 2: Model configuration overrides defaults
            try
            {
                var modelConfig = await chatClientService.GetModelConfigurationAsync(_chatClientConfigName!);
                if (modelConfig.ContextWindowTokens is { } ctxTokens)
                    maxContextTokens = ctxTokens;
                if (modelConfig.CompactionEnabled is { } mcEnabled)
                    enabled = mcEnabled;
                if (modelConfig.CompactionKeepLastN is { } mcKeep)
                    keepLastN = mcKeep;
                if (modelConfig.CompactionThreshold is { } mcThresh)
                    threshold = mcThresh;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not load model configuration for compaction settings fallback");
            }

            // Layer 3: Agent args override model config (prefixed with _)
            if (args.TryGetValue("_CompactionEnabled", out var enabledStr) && bool.TryParse(enabledStr, out var enabledVal))
                enabled = enabledVal;
            if (args.TryGetValue("_CompactionMaxContextTokens", out var maxStr) && int.TryParse(maxStr, out var maxVal))
                maxContextTokens = maxVal;
            if (args.TryGetValue("_CompactionKeepLastN", out var keepStr) && int.TryParse(keepStr, out var keepVal))
                keepLastN = keepVal;
            if (args.TryGetValue("_CompactionThreshold", out var threshStr)
                && double.TryParse(threshStr, System.Globalization.CultureInfo.InvariantCulture, out var threshVal))
                threshold = threshVal;

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
                _customStateCache = await fabrcoreAgentHost.GetCustomStateAsync();
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

            await fabrcoreAgentHost.MergeCustomStateAsync(_pendingStateChanges, _pendingStateDeletes);

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

        // Internal methods for IFabrCoreAgentProxy
        bool IFabrCoreAgentProxy.InternalHasPendingStateChanges => HasPendingStateChanges;

        async Task IFabrCoreAgentProxy.InternalFlushStateAsync()
        {
            await FlushStateAsync();
        }

        async Task IFabrCoreAgentProxy.InternalDisposeAsync()
        {
            if (_mcpClients.Count == 0)
                return;

            logger.LogInformation("Disposing {Count} MCP client(s) for agent '{Handle}'",
                _mcpClients.Count, config.Handle);

            foreach (var client in _mcpClients)
            {
                try
                {
                    await client.DisposeAsync();
                    McpServersDisposedCounter.Add(1,
                        new KeyValuePair<string, object?>("agent.handle", config.Handle));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing MCP client for agent '{Handle}'", config.Handle);
                }
            }

            _mcpClients.Clear();
        }

        #endregion

        public abstract Task<AgentMessage> OnMessage(AgentMessage message);
        public abstract Task OnInitialize();

        /// <summary>
        /// Called when an event message is received on the AgentEvent stream.
        /// Override this method to handle events separately from chat messages.
        /// Default implementation logs and ignores the event.
        /// </summary>
        public virtual Task OnEvent(EventMessage message)
        {
            logger.LogDebug("Event received but not handled - Source: {Source}, Type: {EventType}",
                message.Source, message.Type);
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
            if (_mcpClients.Count > 0)
            {
                return new Dictionary<string, string>
                {
                    ["McpServerConnections"] = _mcpClients.Count.ToString()
                };
            }

            return null;
        }

        async Task IFabrCoreAgentProxy.InternalInitialize()
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

        async Task<ProxyHealthStatus> IFabrCoreAgentProxy.InternalGetHealth(HealthDetailLevel detailLevel)
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

        async Task<AgentMessage> IFabrCoreAgentProxy.InternalOnMessage(AgentMessage message)
        {
            using var activity = ActivitySource.StartActivity("InternalOnMessage", ActivityKind.Server);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);
            activity?.SetTag("message.kind", message.Kind.ToString());

            logger.LogTrace("Agent proxy processing message - From: {FromHandle}, To: {ToHandle}",
                message.FromHandle, message.ToHandle);

            _activeMessage = message;
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                AgentMessage response;
                using (var llmScope = LlmUsageScope.Begin())
                {
                    response = await OnMessage(message);

                    // Auto-compact chat history if threshold exceeded
                    await TryCompactAsync();

                    // Attach LLM usage metrics to the response
                    if (llmScope.CallCount > 0)
                    {
                        response.Args ??= new Dictionary<string, string>();
                        llmScope.ApplyTo(response.Args);
                    }
                }

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

        async Task IFabrCoreAgentProxy.InternalOnEvent(EventMessage message)
        {
            using var activity = ActivitySource.StartActivity("InternalOnEvent", ActivityKind.Server);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);
            activity?.SetTag("event.source", message.Source);
            activity?.SetTag("event.type", message.Type);

            logger.LogTrace("Agent proxy processing event - Source: {Source}, Type: {EventType}",
                message.Source, message.Type);

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
                logger.LogError(ex, "Error processing event in agent proxy - Source: {Source}, Type: {EventType}",
                    message.Source, message.Type);
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
