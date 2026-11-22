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
        internal bool InternalIsProcessingAdmin { get; }
        internal Task InternalOnAdminMessage(AdminDiagnosticContext context, CancellationToken cancellationToken);
        internal Task InternalInitialize();
        internal Task<AgentMessage> InternalOnMessage(AgentMessage message);
        internal Task InternalOnEvent(EventMessage message);
        internal Task<ProxyHealthStatus> InternalGetHealth(HealthDetailLevel detailLevel);
        internal Task InternalReset();
        internal Task InternalFlushStateAsync();
        internal Task InternalDisposeAsync();
        internal bool InternalHasPendingStateChanges { get; }

        /// <summary>True when the proxy is currently executing an OnMessage call.</summary>
        internal bool InternalIsProcessingMessage { get; }

        /// <summary>How long the current primary OnMessage has been running. Zero if not processing.</summary>
        internal TimeSpan InternalProcessingElapsed { get; }

        // Channel queries use the exact nonblank channel; blank channels share one key.
        internal bool InternalIsProcessingMessageForChannel(string? channel);
        internal TimeSpan InternalProcessingElapsedForChannel(string? channel);
        internal AgentMessage? InternalActiveMessageForChannel(string? channel);
        internal string? InternalStatusMessageForChannel(string? channel);

        /// <summary>
        /// Lightweight handler invoked when a new message arrives while OnMessage is already running.
        /// Routes to the virtual OnMessageBusy method.
        /// </summary>
        internal Task<AgentMessage> InternalOnMessageBusy(AgentMessage message);

        Task OnInitialize();
        Task<AgentMessage> OnMessage(AgentMessage message);

        /// <summary>
        /// Called when a new message arrives while the agent is already processing a message.
        /// The default implementation returns a standard "busy" response.
        /// Override to implement custom busy-state handling (e.g., acknowledge receipt,
        /// reject duplicates, provide status, or perform state-safe read-only work).
        /// IMPORTANT: Do not mutate shared agent state in this method — the primary OnMessage
        /// may be mid-execution at any await point.
        /// </summary>
        /// <param name="message">The incoming message that arrived while busy.</param>
        /// <returns>A response message to send back to the caller.</returns>
        Task<AgentMessage> OnMessageBusy(AgentMessage message);

        /// <summary>
        /// Called before the agent is reset and reconfigured.
        /// Override to perform custom cleanup (e.g., closing connections, clearing caches).
        /// The base implementation does nothing. After this returns, all state is cleared
        /// and ConfigureAgent is called with ForceReconfigure=true.
        /// </summary>
        Task OnReset();

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


    public abstract partial class FabrCoreAgentProxy : IFabrCoreAgentProxy
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

        private static readonly Counter<long> BusyMessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.agent.proxy.messages.busy",
            description: "Number of messages routed to OnMessageBusy because the agent was already processing");

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

        private sealed class MessageTurn
        {
            public AgentMessage? Message { get; init; }
            public long Started { get; } = Stopwatch.GetTimestamp();
            public string? Status;
        }

        private readonly object _turnLock = new();
        private readonly List<MessageTurn> _activeTurns = new();
        private readonly AsyncLocal<MessageTurn?> _currentTurn = new();
        private volatile string? _statusMessage;

        private MessageTurn? FindTurn(string? channel)
        {
            var key = NormalizeConversationChannel(channel);
            lock (_turnLock)
                return _activeTurns.LastOrDefault(t => NormalizeConversationChannel(t.Message?.Channel) == key);
        }

        private MessageTurn? LatestTurn()
        {
            lock (_turnLock) return _activeTurns.LastOrDefault();
        }

        /// <summary>The primary message for this execution, or the latest active primary outside a turn.</summary>
        protected AgentMessage? ActiveMessage => (_currentTurn.Value ?? LatestTurn())?.Message;

        /// <summary>Sets the current turn's heartbeat status, or the legacy fallback outside a turn.</summary>
        protected void SetStatusMessage(string? message)
        {
            var turn = _currentTurn.Value;
            if (turn is null) _statusMessage = message;
            else Volatile.Write(ref turn.Status, message);
        }

        /// <inheritdoc/>
        string? IFabrCoreAgentProxy.StatusMessage
        {
            get
            {
                var turn = _currentTurn.Value ?? LatestTurn();
                return turn is null ? _statusMessage : Volatile.Read(ref turn.Status);
            }
            set => SetStatusMessage(value);
        }

        // A writer excludes all handlers. Preflight is opportunistic so another active handler
        // never prevents different channels from entering; postflight runs at a quiet point.
        private sealed class HistoryGate
        {
            private readonly object _sync = new();
            private int _readers;
            private bool _writer;
            private TaskCompletionSource<bool> _changed = NewSignal();
            private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
            private void Signal() { var previous = _changed; _changed = NewSignal(); previous.TrySetResult(true); }

            public bool TryEnterWriter()
            {
                lock (_sync)
                {
                    if (_writer || _readers != 0) return false;
                    _writer = true;
                    return true;
                }
            }

            public async Task EnterReaderAsync()
            {
                while (true)
                {
                    Task wait;
                    lock (_sync)
                    {
                        if (!_writer) { _readers++; return; }
                        wait = _changed.Task;
                    }
                    await wait;
                }
            }

            public void ExitReader()
            {
                lock (_sync) { _readers--; if (_readers == 0) Signal(); }
            }

            public void ExitWriter() { lock (_sync) { _writer = false; Signal(); } }
        }

        private readonly HistoryGate _historyGate = new();

        // MCP client lifecycle tracking
        private readonly List<McpClient> _mcpClients = new();

        // Compaction plumbing — lazily initialized per history provider.
        private sealed class ChatHistoryCompactionRegistration
        {
            public required FabrCoreChatHistoryProvider Provider { get; init; }
            public required string ChatClientConfigName { get; init; }
            public Func<FabrCoreChatHistoryProvider, CompactionConfig, Task<CompactionResult?>>? HistoryCompaction { get; init; }
            public ContextCompactionConfig? ContextCompactionConfig { get; set; }
            public CompactionConfig? CompactionConfig { get; set; }
            public ProjectionConfig? ProjectionConfig { get; set; }
            public ChatRunSafetyConfig? RunSafetyConfig { get; set; }
            public CompactionLadder? Ladder { get; set; }
            public bool Initialized { get; set; }
            public int AppliedStatedLimit { get; set; }
        }

        private sealed class CompactionRegistrations
        {
            private readonly object _sync = new();
            private readonly List<ChatHistoryCompactionRegistration> _items = new();
            public void Add(ChatHistoryCompactionRegistration registration) { lock (_sync) _items.Add(registration); }
            public int Count { get { lock (_sync) return _items.Count; } }
            public ChatHistoryCompactionRegistration[] ToArray() { lock (_sync) return _items.ToArray(); }
        }

        private readonly CompactionRegistrations _chatHistoryCompactionRegistrations = new();
        private readonly SemaphoreSlim _compactionInitializationGate = new(1, 1);
        private FabrCoreChatHistoryProvider? _chatHistoryProvider;
        private string? _chatClientConfigName;
        private CompactionService? _compactionService;
        private CompactionConfig? _compactionConfig;
        private ProjectionConfig? _projectionConfig;
        private ContextCompactionConfig? _contextCompactionConfig;
        private CompactionLadder? _compactionLadder;

        // What provider rejections have taught this activation, per model configuration.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelContextLimits> _contextLimits =
            new(StringComparer.OrdinalIgnoreCase);
        private ModelContextLimitRegistry? _contextLimitRegistry;

        /// <summary>The lazily-resolved history-compaction service, available after compaction has been initialized.</summary>
        protected CompactionService? CompactionServiceInstance => _compactionService;

        /// <summary>The chat client configuration name used for history-compaction LLM calls.</summary>
        protected string? CompactionChatClientConfigName => _chatClientConfigName;

        /// <summary>
        /// The resolved compaction ladder for the most recently initialized history provider, or
        /// <see langword="null"/> before compaction has been initialized. Useful for diagnostics and tests.
        /// </summary>
        protected CompactionLadder? CompactionLadderInfo => _compactionLadder;

        /// <summary>Optional verifiable execution recorder for agent, plugin, and external-effect evidence.</summary>
        protected FabrCore.Core.VerifiableExecution.IVerifiableExecutionContext? VerifiableExecution =>
            serviceProvider.GetService<FabrCore.Core.VerifiableExecution.IVerifiableExecutionContext>();

        /// <summary>True when a verifiable execution context is available from the host.</summary>
        protected bool IsVerifiableExecutionEnabled => VerifiableExecution is not null;

        // Custom state persistence
        private Dictionary<string, JsonElement>? _customStateCache;
        private readonly Dictionary<string, JsonElement> _pendingStateChanges = new();
        private readonly HashSet<string> _pendingStateDeletes = new();
        private bool _customStateLoaded;
        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _stateLoadGate = new(1, 1);
        private readonly SemaphoreSlim _stateMergeGate = new(1, 1);
        private readonly Dictionary<string, long> _stateVersions = new();
        private long _nextStateVersion;

        private const string ConversationTrackingEnabledArg = "_ConversationTrackingEnabled";
        private const string ConversationWorkerModelArg = "_ConversationWorkerModel";
        private const string ConversationCatalogStateKey = "_ConversationCatalog";
        private const int ConversationDescriptionMaxWords = 74;
        private readonly SemaphoreSlim _conversationCatalogLock = new(1, 1);
        private readonly HashSet<string> _conversationDescriptionsInFlight = new(StringComparer.Ordinal);
        private JsonElement? _conversationCatalogPendingWrite;

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

        /// <summary>
        /// Sends a one-way text message to the principal that owns this agent. If the
        /// principal has no live observer, the host can route it through an installed
        /// principal message relay.
        /// </summary>
        protected Task SendToUserAsync(
            string message,
            string? messageType = null,
            PrincipalDeliveryTarget? target = null)
        {
            ArgumentNullException.ThrowIfNull(message);

            return SendToUserAsync(new AgentMessage
            {
                Message = message,
                MessageType = messageType,
                DeliveryTarget = target
            });
        }

        /// <summary>
        /// Sends a structured one-way message to the principal that owns this agent.
        /// The message's data, files, state, arguments, and delivery target are retained.
        /// </summary>
        protected Task SendToUserAsync(AgentMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var userHandle = fabrcoreAgentHost.GetUserHandle();
            if (string.IsNullOrWhiteSpace(userHandle))
            {
                throw new InvalidOperationException(
                    "This agent is not associated with a principal user handle.");
            }

            ValidateDeliveryTarget(message.DeliveryTarget);
            message.ToHandle = userHandle;
            message.Kind = MessageKind.OneWay;
            return fabrcoreAgentHost.SendMessage(message);
        }

        private static void ValidateDeliveryTarget(PrincipalDeliveryTarget? target)
        {
            if (target is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(target.Channel))
            {
                throw new ArgumentException(
                    "A delivery target must specify a channel.",
                    nameof(target));
            }

            if (target.Channel.Length > 128 || target.EndpointId?.Length > 512)
            {
                throw new ArgumentException(
                    "The delivery target channel or endpoint identifier is too long.",
                    nameof(target));
            }
        }

        /// <summary>Resolve a configured Foundry Decision deployment by its FabrCore alias.</summary>
        protected Task<IFabrCoreDecisionClient> GetDecisionClient(string name) => chatClientService.GetDecisionClient(name);

        protected async Task<Microsoft.Extensions.AI.IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
        {
            var client = await chatClientService.GetChatClient(name, networkTimeoutSeconds);
            var monitor = serviceProvider.GetService<FabrCore.Core.Monitoring.IAgentMessageMonitor>();
            var verifiableExecution = serviceProvider.GetService<FabrCore.Core.VerifiableExecution.IVerifiableExecutionContext>();
            await BuildContextCompactionConfigAsync(name);
            var limits = GetContextLimits(name);
            var tracked = new TokenTrackingChatClient(client, fabrcoreAgentHost.GetHandle(), monitor, verifiableExecution, logger)
            {
                ContextWindowTokens = limits.Configured.MaxContextWindowTokens,
                ReservedOutputTokens = limits.Configured.StatedOutputTokens,
                Limits = limits
            };

            // Outermost, so a rejected attempt and its retry are each a tracked, guarded call.
            return new ContextOverflowRecoveryChatClient(tracked, limits, logger);
        }

        /// <summary>
        /// The limits handle for a model configuration. The registry behind it is shared across the
        /// host when registered; otherwise what is learned stays within this activation.
        /// </summary>
        private ModelContextLimits GetContextLimits(string chatClientConfigName)
        {
            var registry = _contextLimitRegistry ??=
                serviceProvider.GetService<ModelContextLimitRegistry>() ?? new ModelContextLimitRegistry();
            return _contextLimits.GetOrAdd(chatClientConfigName, name => new ModelContextLimits(name, registry));
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

            // Layer 1 of the ladder: bound every call in the tool loop with protected tool-output excerpts.
            // Added before configureOptions so a caller supplying its own providers can still see and
            // reorder ours rather than silently replacing them.
            var contextCompactionProvider = await TryCreateContextCompactionProviderAsync(chatClientConfigName);
            if (contextCompactionProvider is not null)
            {
                options.AIContextProviders = options.AIContextProviders is { } existing
                    ? [.. existing, contextCompactionProvider]
                    : [contextCompactionProvider];
            }

            // Allow caller to configure options (including AIContextProviders)
            configureOptions?.Invoke(options);

#pragma warning disable MAAI001
            var perCallProviders = options.AIContextProviders?.OfType<Microsoft.Agents.AI.Compaction.CompactionProvider>().ToList();
            if (perCallProviders is { Count: > 0 })
            {
                options.AIContextProviders = options.AIContextProviders!
                    .Where(p => p is not Microsoft.Agents.AI.Compaction.CompactionProvider).ToList();
                var clientBuilder = chatClient.AsBuilder().UseFunctionInvocation(serviceProvider.GetService<ILoggerFactory>());
                foreach (var provider in perCallProviders) clientBuilder.UseAIContextProviders(provider);
                chatClient = clientBuilder.Build(serviceProvider);
            }
#pragma warning restore MAAI001

            var agent = new ChatClientAgent(chatClient, options)
                .AsBuilder()
                .UseOpenTelemetry(null, cfg => cfg.EnableSensitiveData = true)
                .Build(serviceProvider);

            var session = await agent.CreateSessionAsync();

            // Auto-store for compaction support
            _chatHistoryProvider = historyProvider;
            _chatClientConfigName = chatClientConfigName;
            var compactionRegistration = new ChatHistoryCompactionRegistration
            {
                Provider = historyProvider,
                ChatClientConfigName = chatClientConfigName
            };
            _chatHistoryCompactionRegistrations.Add(compactionRegistration);

            // Eagerly initialize compaction + projection config so the sliding-window
            // projection is active for the very first ProvideChatHistoryAsync call.
            // Without this, the first LLM request after rehydration would see the full
            // unbounded history.
            await EnsureCompactionInitializedAsync(compactionRegistration);

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
            var scope = await registry.ResolveConfiguredToolScopeAsync(serviceProvider, config.Plugins, config.Tools, config, fabrcoreAgentHost);
            _configuredToolScopes.Add(scope);
            var tools = scope.Tools.ToList();

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

            HttpClient? authenticatedClient = null;
            if (!string.IsNullOrWhiteSpace(mcpConfig.Connection))
            {
                if (mcpConfig.TransportType != McpTransportType.Http || string.IsNullOrWhiteSpace(mcpConfig.Resource))
                    throw new ArgumentException("Authenticated MCP requires HTTP transport and a resource reference.");
                if (mcpConfig.Headers.Keys.Any(k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Connection-backed MCP cannot also configure an Authorization header.");
                authenticatedClient = await Connections.GetHttpClientAsync(mcpConfig.Connection, mcpConfig.Resource);
            }
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

                McpTransportType.Http when authenticatedClient is not null => new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = mcpConfig.Name,
                    Endpoint = new Uri(mcpConfig.Url ?? throw new ArgumentException("MCP URL is required")),
                    AdditionalHeaders = mcpConfig.Headers
                }, authenticatedClient, loggerFactory, ownsHttpClient: true),
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

        /// <summary>Default history-compaction threshold when context compaction is not active.</summary>
        private const double DefaultHistoryThreshold = 0.75;

        /// <summary>
        /// Default durable threshold within the input working set. Summarize between turns before
        /// reaching the tighter in-run tool-excerpt target.
        /// </summary>
        private const double DefaultHistoryThresholdWithContextCompaction = 0.7;

        /// <summary>Default projection threshold when projection is demoted to a fuse behind context compaction.</summary>
        private const double DefaultProjectionFuseThreshold = 0.9;

        /// <summary>
        /// Called when <b>history compaction</b> (layer 2) is triggered — the persisted thread has grown
        /// past its threshold and needs summarizing and rewriting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is not the hook for bounding a single LLM call. That is layer 1, context compaction, which
        /// runs before every call in the tool loop, costs no LLM call, and never touches storage — see
        /// <see cref="ContextCompaction"/>. By the time this is called, layer 1 has already been evicting
        /// and truncating for a while and the durable thread still needs consolidating.
        /// </para>
        /// <para>
        /// Override to change how that consolidation happens — different prompts, a different model, or a
        /// different summarization strategy. <c>FabrCore.Services.Memory</c> overrides it to convert
        /// conversation mass into durable graph memories before summarizing. The default implementation
        /// uses <see cref="CompactionService"/>'s map-reduce summarizer.
        /// </para>
        /// </remarks>
        /// <param name="chatHistoryProvider">The chat history provider containing messages to compact.</param>
        /// <param name="compactionConfig">History compaction configuration (thresholds, keep count, etc.).</param>
        /// <param name="estimatedTokens">The estimated stored token count that triggered compaction.</param>
        /// <returns>The compaction result, or null if compaction was skipped.</returns>
        public virtual async Task<CompactionResult?> OnCompaction(
            FabrCoreChatHistoryProvider chatHistoryProvider,
            CompactionConfig compactionConfig,
            int estimatedTokens = 0)
        {
            var customCompaction = _chatHistoryCompactionRegistrations.ToArray()
                .LastOrDefault(r => ReferenceEquals(r.Provider, chatHistoryProvider))?.HistoryCompaction;
            if (customCompaction is null && (_compactionService is null || _chatClientConfigName is null))
                return null;

            // Set status so the grain's heartbeat loop shows "Compacting.." instead of "Thinking.."
            SetStatusMessage("Compacting..");

            // Exempt the summarization call from the turn budget: history compaction exists to reduce
            // spend and must never be aborted by the spend limit it is working to keep the run under.
            using var _ = ChatRunSafetyScope.Current?.BeginHistoryCompaction();
            try
            {
                if (customCompaction is not null)
                    return await customCompaction(chatHistoryProvider, compactionConfig);
                return await _compactionService!.CompactIfNeededAsync(
                    chatHistoryProvider, compactionConfig, _chatClientConfigName!);
            }
            finally
            {
                SetStatusMessage(null);
            }
        }

        /// <summary>
        /// Builds the layer 1 context-compaction provider for a model configuration, or returns
        /// <see langword="null"/> when the model configuration does not supply a context window (the
        /// output reserve is derived when no output limit is set). Callers add the result to their
        /// agent's context providers.
        /// </summary>
        private async Task<AIContextProvider?> TryCreateContextCompactionProviderAsync(
            string chatClientConfigName)
        {
            var contextConfig = await BuildContextCompactionConfigAsync(chatClientConfigName);
            _contextCompactionConfig = contextConfig;

            // Built over the live limits so a cap learned mid-run tightens the next call's thresholds.
            var limits = GetContextLimits(chatClientConfigName);
            var provider = ContextCompaction.TryCreateProvider(
                limits.Configured, () => limits.Effective(forTrim: true), loggerFactory);

            if (provider is null && contextConfig.Enabled)
            {
                logger.LogWarning(
                    "Context compaction is not configured for '{Handle}' (model config '{ModelConfig}'): ContextWindowTokens={Window}, MaxOutputTokens={Output}, WorkingSetTokens={WorkingSet}, EvictThreshold={Evict}, TruncateThreshold={Truncate}. " +
                    "The agent runs with no in-run context bound — only history compaction, the projection fuse, and the run-safety stop protect it. " +
                    "Set a positive ContextWindowTokens (MaxOutputTokens is optional but must be smaller than the window when set); working sets must be positive and thresholds ordered in (0, 1].",
                    config.Handle, chatClientConfigName,
                    contextConfig.MaxContextWindowTokens, contextConfig.MaxOutputTokens, contextConfig.WorkingSetTokens,
                    contextConfig.EvictThreshold, contextConfig.TruncateThreshold);
            }

            return provider;
        }

        /// <summary>
        /// Lazily resolves the history-compaction service from DI, resolves the full compaction ladder,
        /// and attaches the projection fuse to the chat history provider so it is active before the first
        /// LLM call. Safe to call multiple times; subsequent calls are no-ops.
        /// </summary>
        private async Task EnsureCompactionInitializedAsync()
        {
            foreach (var registration in _chatHistoryCompactionRegistrations.ToArray())
            {
                await EnsureCompactionInitializedAsync(registration);
            }
        }

        private async Task EnsureCompactionInitializedAsync(ChatHistoryCompactionRegistration registration)
        {
            await _compactionInitializationGate.WaitAsync();
            try { await InitializeCompactionCoreAsync(registration); }
            finally { _compactionInitializationGate.Release(); }
        }

        private async Task InitializeCompactionCoreAsync(ChatHistoryCompactionRegistration registration)
        {
            // Resolved once per activation, and again whenever a provider states a different limit
            // for this model, so history, the fuse and the stop follow a learned cap on the next turn.
            if (registration.Initialized
                && registration.AppliedStatedLimit == (GetContextLimits(registration.ChatClientConfigName).StatedLimit ?? 0))
                return;

            _compactionService ??= serviceProvider.GetService<CompactionService>();

            // Resolve top-down: the context rungs decide where the history rung defaults to, which in
            // turn decides where the fuse sits. Anything explicitly configured still wins at each step.
            // Built per registration rather than reusing _contextCompactionConfig — an agent can hold
            // providers on different model configs, and each needs its own window and output reserve.
            registration.ContextCompactionConfig =
                await BuildContextCompactionConfigAsync(registration.ChatClientConfigName);
            registration.CompactionConfig = await BuildCompactionConfigAsync(
                registration.ChatClientConfigName, registration.ContextCompactionConfig);
            registration.ProjectionConfig = BuildProjectionConfig(
                registration.CompactionConfig, registration.ContextCompactionConfig);
            registration.RunSafetyConfig = await BuildRunSafetyConfigAsync(
                registration.ChatClientConfigName, registration.CompactionConfig, registration.ContextCompactionConfig);
            registration.Provider.ActiveProjection = registration.ProjectionConfig;

            registration.Ladder = new CompactionLadder
            {
                Context = registration.ContextCompactionConfig,
                History = registration.CompactionConfig,
                Projection = registration.ProjectionConfig,
                RunSafety = registration.RunSafetyConfig
            };

            registration.Initialized = true;
            registration.AppliedStatedLimit = GetContextLimits(registration.ChatClientConfigName).StatedLimit ?? 0;

            // Keep the legacy fields pointed at the most recently initialized provider
            // for overrides that inspect CompactionChatClientConfigName.
            _chatHistoryProvider = registration.Provider;
            _chatClientConfigName = registration.ChatClientConfigName;
            _compactionConfig = registration.CompactionConfig;
            _projectionConfig = registration.ProjectionConfig;
            _contextCompactionConfig = registration.ContextCompactionConfig;
            _compactionLadder = registration.Ladder;

            // One line, every rung, resolved values. Most compaction confusion is "which value won" —
            // answer it before anyone has to ask.
            logger.LogInformation(
                "Compaction ladder for '{Handle}' provider '{ThreadId}' (model config '{ModelConfig}'): {Ladder}",
                config.Handle, registration.Provider.ThreadId, registration.ChatClientConfigName,
                registration.Ladder.Describe());

            if (registration.Ladder.IsOutOfOrder)
            {
                logger.LogWarning(
                    "Compaction budgets for '{Handle}' are out of order: {Ladder}. Projection or request safety may stop " +
                    "before durable summarization triggers. Check history, projection and request budget settings.",
                    config.Handle, registration.Ladder.Describe());
            }

            if (registration.ContextCompactionConfig.IsUsable && !registration.CompactionConfig.Enabled)
            {
                logger.LogWarning(
                    "History compaction is disabled for '{Handle}' while context compaction is active. " +
                    "The model stays within its window, but stored history in thread '{ThreadId}' will grow without bound " +
                    "and every state write rewrites the whole grain blob.",
                    config.Handle, registration.Provider.ThreadId);
            }

            logger.LogDebug(
                "History compaction for '{Handle}' provider '{ThreadId}': Enabled={Enabled}, MaxContextTokens={MaxTokens}, Threshold={Threshold}, KeepLastN={KeepLastN}, StaleAfterMinutes={Stale}",
                config.Handle, registration.Provider.ThreadId,
                registration.CompactionConfig.Enabled, registration.CompactionConfig.MaxContextTokens, registration.CompactionConfig.Threshold,
                registration.CompactionConfig.KeepLastN, registration.CompactionConfig.StaleAfterMinutes);
            logger.LogDebug(
                "Projection fuse for '{Handle}' provider '{ThreadId}': Enabled={Enabled}, MaxContextTokens={MaxTokens}, Threshold={Threshold}, MinKeepLastN={MinKeep}",
                config.Handle, registration.Provider.ThreadId,
                registration.ProjectionConfig.Enabled, registration.ProjectionConfig.MaxContextTokens, registration.ProjectionConfig.Threshold,
                registration.ProjectionConfig.MinKeepLastN);
        }

        /// <summary>
        /// Attempts to compact the chat history if the token count exceeds the configured threshold.
        /// Called automatically after each OnMessage. Returns null if compaction is not configured or not needed.
        /// On first call, lazily resolves CompactionService from DI and builds CompactionConfig from agent args.
        /// </summary>
        private async Task<CompactionResult?> TryCompactAsync(Func<Task>? onCompacting = null)
        {
            if (_chatHistoryCompactionRegistrations.Count == 0)
                return null;

            CompactionResult? lastResult = null;
            foreach (var registration in _chatHistoryCompactionRegistrations.ToArray())
            {
                var result = await TryCompactAsync(registration, onCompacting);
                if (result is not null)
                    lastResult = result;
            }

            return lastResult;
        }

        private async Task<CompactionResult?> TryCompactAsync(
            ChatHistoryCompactionRegistration registration,
            Func<Task>? onCompacting = null)
        {
            try
            {
                await EnsureCompactionInitializedAsync(registration);

                var compactionConfig = registration.CompactionConfig;
                if (compactionConfig is null || !compactionConfig.Enabled)
                    return null;

                if (compactionConfig.MaxContextTokens <= 0)
                    return null;

                // Check threshold before calling OnCompaction
                if (registration.Provider.HasPendingMessages)
                    await registration.Provider.FlushAsync();

                var messages = await registration.Provider.GetStoredMessagesAsync();
                var estimatedTokens = CompactionService.EstimateTokens(messages);
                var threshold = (int)(compactionConfig.MaxContextTokens * compactionConfig.Threshold);

                if (estimatedTokens <= threshold)
                {
                    logger.LogDebug(
                        "History compaction not needed for '{Handle}' provider '{ThreadId}': ~{EstimatedTokens} estimated stored tokens <= {Threshold} threshold ({MessageCount} messages)",
                        config.Handle, registration.Provider.ThreadId, estimatedTokens, threshold, messages.Count);
                    return null;
                }

                logger.LogInformation(
                    "History compaction needed for '{Handle}' provider '{ThreadId}': ~{EstimatedTokens} estimated stored tokens exceeds {Threshold} threshold ({Ratio:P0} of {Max})",
                    config.Handle, registration.Provider.ThreadId, estimatedTokens, threshold, compactionConfig.Threshold, compactionConfig.MaxContextTokens);

                if (onCompacting is not null)
                    await onCompacting();

                _chatHistoryProvider = registration.Provider;
                _chatClientConfigName = registration.ChatClientConfigName;
                _compactionConfig = compactionConfig;
                _projectionConfig = registration.ProjectionConfig;

                await RecordCompactionEventAsync("history.started", registration, new Dictionary<string, string>
                {
                    ["trigger"] = "post-turn",
                    ["estimated_stored_tokens"] = estimatedTokens.ToString(),
                    ["threshold_tokens"] = threshold.ToString(),
                    ["message_count"] = messages.Count.ToString()
                });

                // Delegate to OnCompaction — only called when threshold is exceeded
                var result = await OnCompaction(registration.Provider, compactionConfig, estimatedTokens);

                if (result?.WasCompacted == true)
                {
                    logger.LogInformation(
                        "History compacted for '{Handle}' provider '{ThreadId}': {Before} → {After} messages (~{TokensBefore} → ~{TokensAfter} tokens)",
                        config.Handle, registration.Provider.ThreadId,
                        result.OriginalMessageCount, result.CompactedMessageCount,
                        result.EstimatedTokensBefore, result.EstimatedTokensAfter);
                }

                await RecordCompactionEventAsync("history.completed", registration, new Dictionary<string, string>
                {
                    ["trigger"] = "post-turn",
                    ["was_compacted"] = (result?.WasCompacted == true).ToString(),
                    ["messages_before"] = (result?.OriginalMessageCount ?? 0).ToString(),
                    ["messages_after"] = (result?.CompactedMessageCount ?? 0).ToString(),
                    ["tokens_before"] = (result?.EstimatedTokensBefore ?? 0).ToString(),
                    ["tokens_after"] = (result?.EstimatedTokensAfter ?? 0).ToString()
                });

                return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "History compaction failed for '{Handle}' provider '{ThreadId}' — continuing without compaction",
                    config.Handle, registration.Provider.ThreadId);

                await RecordCompactionEventAsync("history.failed", registration, new Dictionary<string, string>
                {
                    ["trigger"] = "post-turn",
                    ["error"] = ex.Message
                });

                return null;
            }
        }

        /// <summary>
        /// Records a layer-tagged compaction event on the agent monitor. Event types are
        /// <c>compaction.history.*</c>; layer 1 emits its own OpenTelemetry spans through
        /// <c>CompactionTelemetry</c> and is not duplicated here.
        /// </summary>
        private Task RecordCompactionEventAsync(
            string type,
            ChatHistoryCompactionRegistration registration,
            Dictionary<string, string> args)
        {
            var monitor = serviceProvider.GetService<FabrCore.Core.Monitoring.IAgentMessageMonitor>();
            if (monitor is null)
                return Task.CompletedTask;

            args["thread_id"] = registration.Provider.ThreadId;
            args["model_config"] = registration.ChatClientConfigName;
            args["parent_message_id"] = LlmUsageScope.Current?.ParentMessageId ?? "";

            return monitor.RecordEventAsync(new FabrCore.Core.Monitoring.MonitoredEvent
            {
                AgentHandle = fabrcoreAgentHost.GetHandle(),
                Type = $"compaction.{type}",
                Source = "FabrCore.Sdk",
                Subject = LlmUsageScope.Current?.ParentMessageId,
                Args = args,
                EventTime = DateTimeOffset.UtcNow,
                TraceId = LlmUsageScope.Current?.TraceId
            });
        }

        /// <summary>
        /// Pre-flight compaction: runs *before* <see cref="OnMessage"/> when estimated
        /// stored tokens already exceed the compaction threshold. This prevents an
        /// oversized persisted thread from reaching the provider before post-message
        /// compaction has a chance to run. Projection still acts as the hard safety net
        /// regardless of whether preflight ran.
        /// </summary>
        private async Task<CompactionResult?> TryPreflightCompactAsync()
        {
            if (_chatHistoryCompactionRegistrations.Count == 0)
                return null;

            CompactionResult? lastResult = null;
            foreach (var registration in _chatHistoryCompactionRegistrations.ToArray())
            {
                var result = await TryPreflightCompactAsync(registration);
                if (result is not null)
                    lastResult = result;
            }

            return lastResult;
        }

        private async Task<CompactionResult?> TryPreflightCompactAsync(ChatHistoryCompactionRegistration registration)
        {
            try
            {
                await EnsureCompactionInitializedAsync(registration);

                var compactionConfig = registration.CompactionConfig;
                if (compactionConfig is null || !compactionConfig.Enabled)
                    return null;
                if (compactionConfig.MaxContextTokens <= 0)
                    return null;
                if (compactionConfig.StaleAfterMinutes <= 0)
                    return null;

                if (registration.Provider.HasPendingMessages)
                    await registration.Provider.FlushAsync();

                var messages = await registration.Provider.GetStoredMessagesAsync();
                if (messages.Count == 0)
                    return null;

                var estimatedTokens = CompactionService.EstimateTokens(messages);
                var threshold = (int)(compactionConfig.MaxContextTokens * compactionConfig.Threshold);
                if (estimatedTokens <= threshold)
                {
                    logger.LogDebug(
                        "Preflight history compaction skipped for '{Handle}' provider '{ThreadId}': stored history under threshold (~{Tokens} <= {Threshold})",
                        config.Handle, registration.Provider.ThreadId, estimatedTokens, threshold);
                    return null;
                }

                var newest = messages[messages.Count - 1].Timestamp;
                var newestAge = DateTime.UtcNow - newest;
                logger.LogInformation(
                    "Preflight history compaction for '{Handle}' provider '{ThreadId}': stored history has ~{Tokens} estimated tokens (>{Threshold}); newest message age {Minutes:F1}m — compacting before LLM call",
                    config.Handle, registration.Provider.ThreadId, estimatedTokens, threshold, newestAge.TotalMinutes);

                _chatHistoryProvider = registration.Provider;
                _chatClientConfigName = registration.ChatClientConfigName;
                _compactionConfig = compactionConfig;
                _projectionConfig = registration.ProjectionConfig;

                await RecordCompactionEventAsync("history.started", registration, new Dictionary<string, string>
                {
                    ["trigger"] = "preflight",
                    ["estimated_stored_tokens"] = estimatedTokens.ToString(),
                    ["threshold_tokens"] = threshold.ToString(),
                    ["newest_message_age_minutes"] = newestAge.TotalMinutes.ToString("F1")
                });

                var result = await OnCompaction(registration.Provider, compactionConfig, estimatedTokens);

                if (result?.WasCompacted == true)
                {
                    logger.LogInformation(
                        "Preflight history compaction complete for '{Handle}' provider '{ThreadId}': {Before} → {After} messages (~{TokensBefore} → ~{TokensAfter} tokens)",
                        config.Handle, registration.Provider.ThreadId,
                        result.OriginalMessageCount, result.CompactedMessageCount,
                        result.EstimatedTokensBefore, result.EstimatedTokensAfter);
                }

                await RecordCompactionEventAsync("history.completed", registration, new Dictionary<string, string>
                {
                    ["trigger"] = "preflight",
                    ["was_compacted"] = (result?.WasCompacted == true).ToString(),
                    ["messages_before"] = (result?.OriginalMessageCount ?? 0).ToString(),
                    ["messages_after"] = (result?.CompactedMessageCount ?? 0).ToString(),
                    ["tokens_before"] = (result?.EstimatedTokensBefore ?? 0).ToString(),
                    ["tokens_after"] = (result?.EstimatedTokensAfter ?? 0).ToString()
                });

                return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Preflight history compaction failed for '{Handle}' provider '{ThreadId}' — continuing without compaction (the projection fuse will still protect the call)",
                    config.Handle, registration.Provider.ThreadId);

                await RecordCompactionEventAsync("history.failed", registration, new Dictionary<string, string>
                {
                    ["trigger"] = "preflight",
                    ["error"] = ex.Message
                });

                return null;
            }
        }

        /// <summary>
        /// Resolves layer 1 (context compaction) from defaults → model configuration → agent args.
        /// </summary>
        private async Task<ContextCompactionConfig> BuildContextCompactionConfigAsync(string chatClientConfigName)
        {
            var args = config.Args ?? new Dictionary<string, string>();

            // Defaults
            var enabled = true;
            var windowTokens = 0;
            var outputTokens = 0;
            int? workingSetTokens = null;
            var evictThreshold = ContextCompaction.DefaultEvictThreshold;
            var truncateThreshold = ContextCompaction.DefaultTruncateThreshold;

            // Model configuration overrides defaults
            ModelConfiguration? resolvedModel = null;
            try
            {
                var modelConfig = resolvedModel = await chatClientService.GetModelConfigurationAsync(chatClientConfigName);
                if (modelConfig.ContextWindowTokens is { } ctxTokens)
                    windowTokens = ctxTokens;
                if (modelConfig.MaxOutputTokens is { } outTokens)
                    outputTokens = outTokens;
                workingSetTokens = modelConfig.ContextWorkingSetTokens;
                if (modelConfig.ContextCompactionEnabled is { } mcEnabled)
                    enabled = mcEnabled;
                if (modelConfig.ContextEvictThreshold is { } mcEvict)
                    evictThreshold = mcEvict;
                if (modelConfig.ContextTruncateThreshold is { } mcTruncate)
                    truncateThreshold = mcTruncate;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not load model configuration for context compaction settings fallback");
            }

            // Agent args override model config (prefixed with _)
            if (args.TryGetValue("_ContextCompactionEnabled", out var enabledStr) && bool.TryParse(enabledStr, out var enabledVal))
                enabled = enabledVal;
            if (args.TryGetValue("_ContextWindowTokens", out var windowStr) && int.TryParse(windowStr, out var windowVal))
                windowTokens = windowVal;
            if (args.TryGetValue("_ContextMaxOutputTokens", out var outputStr) && int.TryParse(outputStr, out var outputVal))
                outputTokens = outputVal;
            if (args.TryGetValue("_ContextWorkingSetTokens", out var workingSetStr) && int.TryParse(workingSetStr, out var workingSetVal))
                workingSetTokens = workingSetVal;
            if (args.TryGetValue("_ContextEvictThreshold", out var evictStr)
                && double.TryParse(evictStr, System.Globalization.CultureInfo.InvariantCulture, out var evictVal))
                evictThreshold = evictVal;
            if (args.TryGetValue("_ContextTruncateThreshold", out var truncateStr)
                && double.TryParse(truncateStr, System.Globalization.CultureInfo.InvariantCulture, out var truncateVal))
                truncateThreshold = truncateVal;

            // No output limit configured: reserve output space out of the window so layer 1 still
            // runs. The derived reserve only sizes the trim thresholds — nothing is sent to the
            // provider, and the hard stops keep using the operator-stated value.
            var outputReserveIsDerived = false;
            if (windowTokens > 0 && outputTokens <= 0)
            {
                outputTokens = ContextCompaction.DeriveOutputReserve(windowTokens);
                outputReserveIsDerived = true;
            }

            var configured = new ContextCompactionConfig
            {
                Enabled = enabled,
                MaxContextWindowTokens = windowTokens,
                MaxOutputTokens = outputTokens,
                OutputReserveIsDerived = outputReserveIsDerived,
                WorkingSetTokens = workingSetTokens,
                EvictThreshold = evictThreshold,
                TruncateThreshold = truncateThreshold
            };

            // A limit the provider has stated for this model caps the configured window from here on.
            var limits = GetContextLimits(chatClientConfigName);
            limits.Configure(resolvedModel, configured);
            return limits.Effective(forTrim: false);
        }

        /// <summary>
        /// Resolves layer 2 (history compaction) from defaults → model configuration → agent args.
        /// </summary>
        /// <remarks>
        /// The default threshold depends on whether layer 1 is active: with context compaction bounding
        /// every call, history compaction defaults to 70% of the input working set and acts as the
        /// between-turns consolidator. Without it, history compaction is the first responder and keeps the
        /// original 0.75 default. An explicit setting always wins.
        /// </remarks>
        private async Task<CompactionConfig> BuildCompactionConfigAsync(
            string chatClientConfigName,
            ContextCompactionConfig contextCompaction)
        {
            var args = config.Args ?? new Dictionary<string, string>();

            // Defaults
            var enabled = true;
            var maxContextTokens = 25000;
            var keepLastN = 20;
            var staleAfterMinutes = 60;
            double? threshold = null;

            // Model configuration overrides defaults
            try
            {
                var modelConfig = await chatClientService.GetModelConfigurationAsync(chatClientConfigName);
                if (modelConfig.ContextWindowTokens is { } ctxTokens)
                    maxContextTokens = ctxTokens;
                if (modelConfig.CompactionEnabled is { } mcEnabled)
                    enabled = mcEnabled;
                if (modelConfig.CompactionKeepLastN is { } mcKeep)
                    keepLastN = mcKeep;
                if (modelConfig.CompactionThreshold is { } mcThresh)
                    threshold = mcThresh;
                if (modelConfig.CompactionStaleAfterMinutes is { } mcStale)
                    staleAfterMinutes = mcStale;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not load model configuration for history compaction settings fallback");
            }

            // Align durable history with the input working set, including output reservation.
            if (contextCompaction.IsUsable) maxContextTokens = contextCompaction.InputBudgetTokens;

            // Agent args override model config (prefixed with _)
            if (args.TryGetValue("_CompactionEnabled", out var enabledStr) && bool.TryParse(enabledStr, out var enabledVal))
                enabled = enabledVal;
            if (args.TryGetValue("_CompactionMaxContextTokens", out var maxStr) && int.TryParse(maxStr, out var maxVal))
                maxContextTokens = maxVal;
            if (args.TryGetValue("_CompactionKeepLastN", out var keepStr) && int.TryParse(keepStr, out var keepVal))
                keepLastN = keepVal;
            if (args.TryGetValue("_CompactionThreshold", out var threshStr)
                && double.TryParse(threshStr, System.Globalization.CultureInfo.InvariantCulture, out var threshVal))
                threshold = threshVal;
            if (args.TryGetValue("_CompactionStaleAfterMinutes", out var staleStr) && int.TryParse(staleStr, out var staleVal))
                staleAfterMinutes = staleVal;

            return new CompactionConfig
            {
                Enabled = enabled,
                KeepLastN = keepLastN,
                MaxContextTokens = maxContextTokens,
                SummaryModelConfigurationName = args.GetValueOrDefault("_CompactionModelConfigName"),
                Threshold = threshold ?? (contextCompaction.IsUsable
                    ? DefaultHistoryThresholdWithContextCompaction
                    : DefaultHistoryThreshold),
                StaleAfterMinutes = staleAfterMinutes
            };
        }

        private async Task<ChatRunSafetyConfig> BuildRunSafetyConfigAsync(
            string chatClientConfigName,
            CompactionConfig compactionConfig,
            ContextCompactionConfig contextCompaction)
        {
            var args = config.Args ?? new Dictionary<string, string>();

            var perTurnMaxInputTokens = 0;
            var runawayBudgetBehavior = "StopWithDiagnostic";

            // The stop is the last rung: anchor it to the real window when we know it, so it sits above
            // every compaction rung rather than cutting in underneath them. A derived output reserve
            // is not subtracted — only an operator-stated limit may tighten a hard stop.
            var maxPromptInputTokens = contextCompaction.MaxContextWindowTokens > 0
                ? Math.Max(0, contextCompaction.MaxContextWindowTokens - contextCompaction.StatedOutputTokens)
                : compactionConfig.Enabled ? compactionConfig.MaxContextTokens : 0;

            try
            {
                var modelConfig = await chatClientService.GetModelConfigurationAsync(chatClientConfigName);
                if (modelConfig.PerTurnMaxInputTokens is { } modelPerTurn)
                    perTurnMaxInputTokens = modelPerTurn;
                if (modelConfig.MaxPromptInputTokens is { } modelMaxPrompt)
                    maxPromptInputTokens = modelMaxPrompt;
                if (!string.IsNullOrWhiteSpace(modelConfig.RunawayBudgetBehavior))
                    runawayBudgetBehavior = modelConfig.RunawayBudgetBehavior;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not load model configuration for run safety settings fallback");
            }

            if (args.TryGetValue("_PerTurnMaxInputTokens", out var perTurnStr) && int.TryParse(perTurnStr, out var perTurnVal))
                perTurnMaxInputTokens = perTurnVal;
            if (args.TryGetValue("_MaxPromptInputTokens", out var maxPromptStr) && int.TryParse(maxPromptStr, out var maxPromptVal))
                maxPromptInputTokens = maxPromptVal;
            if (args.TryGetValue("_RunawayBudgetBehavior", out var behaviorStr) && !string.IsNullOrWhiteSpace(behaviorStr))
                runawayBudgetBehavior = behaviorStr;

            return new ChatRunSafetyConfig
            {
                PerTurnMaxInputTokens = perTurnMaxInputTokens,
                MaxPromptInputTokens = maxPromptInputTokens,
                RunawayBudgetBehavior = runawayBudgetBehavior
            };
        }

        /// <summary>
        /// Builds the read-side projection — rung 4 of the ladder.
        /// </summary>
        /// <remarks>
        /// When context compaction is active, projection is demoted to a <b>fuse</b>: anchored to the model
        /// window at <see cref="DefaultProjectionFuseThreshold"/>, well above every other rung, so it only
        /// ever fires in pathological cases. Left at the old inherited values it would clip first and make
        /// the layers above it decorative. Without context compaction it keeps the legacy behaviour of
        /// inheriting the history-compaction settings. Agent-arg overrides use the <c>_Projection*</c> prefix.
        /// </remarks>
        private ProjectionConfig BuildProjectionConfig(
            CompactionConfig compaction,
            ContextCompactionConfig contextCompaction)
        {
            var args = config.Args ?? new Dictionary<string, string>();

            var fuseMode = contextCompaction.IsUsable;

            // Fuse mode: insurance below the provider hard limit. Legacy mode: inherit from compaction.
            var enabled = fuseMode || compaction.Enabled;
            var maxContextTokens = fuseMode ? contextCompaction.MaxContextWindowTokens - contextCompaction.StatedOutputTokens : compaction.MaxContextTokens;
            var threshold = fuseMode ? DefaultProjectionFuseThreshold : compaction.Threshold;
            var minKeepLastN = 2;

            if (args.TryGetValue("_ProjectionEnabled", out var enabledStr) && bool.TryParse(enabledStr, out var enabledVal))
                enabled = enabledVal;
            if (args.TryGetValue("_ProjectionMaxContextTokens", out var maxStr) && int.TryParse(maxStr, out var maxVal))
                maxContextTokens = maxVal;
            if (args.TryGetValue("_ProjectionThreshold", out var threshStr)
                && double.TryParse(threshStr, System.Globalization.CultureInfo.InvariantCulture, out var threshVal))
                threshold = threshVal;
            if (args.TryGetValue("_ProjectionMinKeepLastN", out var minKeepStr) && int.TryParse(minKeepStr, out var minKeepVal))
                minKeepLastN = minKeepVal;

            return new ProjectionConfig
            {
                Enabled = enabled,
                MaxContextTokens = maxContextTokens,
                Threshold = threshold,
                MinKeepLastN = minKeepLastN
            };
        }

        #endregion

        #region Custom State API

        private sealed class ConversationCatalogState
        {
            public Dictionary<string, ConversationCatalogEntry> Conversations { get; set; } = new(StringComparer.Ordinal);
        }

        private sealed class ConversationCatalogEntry
        {
            public string Channel { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public bool IsProvisional { get; set; }
            public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
            public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        }

        private bool IsConversationTrackingEnabled()
        {
            var args = config.Args;
            return args != null
                && args.TryGetValue(ConversationTrackingEnabledArg, out var enabledRaw)
                && bool.TryParse(enabledRaw, out var enabled)
                && enabled;
        }

        private static string NormalizeConversationChannel(string? channel)
            => string.IsNullOrWhiteSpace(channel) ? string.Empty : channel;

        private string ResolveConversationWorkerModel()
        {
            var args = config.Args;
            if (args != null
                && args.TryGetValue(ConversationWorkerModelArg, out var workerModel)
                && !string.IsNullOrWhiteSpace(workerModel))
            {
                return workerModel;
            }

            return !string.IsNullOrWhiteSpace(config.Models) ? config.Models : "default";
        }

#pragma warning disable RS0016 // Public API baseline update is outside this change's permitted scope.
        /// <summary>Gets the configured worker chat client for lightweight agent tasks.</summary>
        /// <remarks>Falls back to the agent's model configuration, then to the default model.</remarks>
        protected Task<IChatClient> GetWorkerChatClientAsync(int networkTimeoutSeconds = 100)
            => GetChatClient(ResolveConversationWorkerModel(), networkTimeoutSeconds);

        /// <summary>Lists persisted conversation metadata, including provisional descriptions.</summary>
        /// <remarks>Returns a snapshot; blank channels are not tracked.</remarks>
        protected async Task<IReadOnlyList<ConversationMetadata>> GetConversationsAsync()
        {
            await _conversationCatalogLock.WaitAsync();
            try
            {
                var catalog = await LoadConversationCatalogAsync();
                return catalog.Conversations.Values.Select(entry => new ConversationMetadata
                {
                    Channel = entry.Channel,
                    Description = entry.Description,
                    IsProvisional = entry.IsProvisional,
                    CreatedAtUtc = entry.CreatedAtUtc,
                    UpdatedAtUtc = entry.UpdatedAtUtc
                }).ToArray();
            }
            finally
            {
                _conversationCatalogLock.Release();
            }
        }

#pragma warning restore RS0016

        // Read and write the reserved catalog independently of the general-purpose pending state cache.
        // The lock serializes the whole read/merge/write, but is never held during a worker call.
        private async Task<ConversationCatalogState> LoadConversationCatalogAsync()
        {
            JsonElement? pending;
            lock (_stateLock) pending = _conversationCatalogPendingWrite;
            if (pending is { } pendingValue)
                return pendingValue.Deserialize<ConversationCatalogState>() ?? new ConversationCatalogState();
            var state = await fabrcoreAgentHost.GetCustomStateAsync();
            if (!state.TryGetValue(ConversationCatalogStateKey, out var value))
                return new ConversationCatalogState();

            return value.Deserialize<ConversationCatalogState>() ?? new ConversationCatalogState();
        }

        private async Task SaveConversationCatalogAsync(ConversationCatalogState catalog)
        {
            lock (_stateLock) _conversationCatalogPendingWrite = JsonSerializer.SerializeToElement(catalog);
            await RetryConversationCatalogWriteAsync();
        }

        private async Task RetryConversationCatalogWriteAsync()
        {
            JsonElement? pending;
            lock (_stateLock) pending = _conversationCatalogPendingWrite;
            if (pending is not { } value) return;

            await _stateMergeGate.WaitAsync();
            try
            {
                await fabrcoreAgentHost.MergeCustomStateAsync(
                    new Dictionary<string, JsonElement> { [ConversationCatalogStateKey] = value },
                    Array.Empty<string>());
                lock (_stateLock)
                {
                    _conversationCatalogPendingWrite = null;
                    if (_customStateCache is not null) _customStateCache[ConversationCatalogStateKey] = value;
                }
            }
            finally { _stateMergeGate.Release(); }
        }

        private static string ExtractFirstSentence(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "New conversation.";
            }

            var trimmed = message.Trim();
            var sentenceBreak = trimmed.IndexOfAny(['.', '!', '?', '\r', '\n']);
            var sentence = sentenceBreak >= 0 ? trimmed[..(sentenceBreak + 1)] : trimmed;
            sentence = sentence.Trim();
            return string.IsNullOrWhiteSpace(sentence) ? "New conversation." : sentence;
        }

        private static string LimitWords(string text, int maxWords)
        {
            if (string.IsNullOrWhiteSpace(text) || maxWords <= 0)
            {
                return string.Empty;
            }

            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= maxWords)
            {
                return string.Join(' ', parts);
            }

            return string.Join(' ', parts.Take(maxWords));
        }

        private static string NormalizeConversationDescription(string? text)
        {
            var compact = LimitWords(text?.Trim() ?? string.Empty, ConversationDescriptionMaxWords);
            return string.IsNullOrWhiteSpace(compact) ? string.Empty : compact;
        }

        private async Task<string?> TryGenerateConversationDescriptionAsync(string channel, string firstMessage)
        {
            var workerModel = ResolveConversationWorkerModel();
            var prompt = string.IsNullOrWhiteSpace(firstMessage) ? "No conversation text was provided." : firstMessage;

            try
            {
                var parentSafety = ChatRunSafetyScope.Current;
                using var workerSafety = parentSafety is null ? null : ChatRunSafetyScope.Begin(
                    parentSafety.AgentHandle, parentSafety.ParentMessageId, parentSafety.TraceId,
                    parentSafety.Config, serviceProvider.GetService<FabrCore.Core.Monitoring.IAgentMessageMonitor>(), logger);
                try
                {
                var chatClient = await GetWorkerChatClientAsync();
                var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System,
                        "Write one concise conversation title-style description with at most 74 words. " +
                        "Return plain text only, no bullets, no markdown, no labels."),
                    new ChatMessage(ChatRole.User, prompt)
                ],
                    new ChatOptions { MaxOutputTokens = 120 });

                var description = NormalizeConversationDescription(response.Text);
                return string.IsNullOrWhiteSpace(description) ? null : description;
                }
                finally
                {
                    if (parentSafety is not null && workerSafety is not null)
                    {
                        for (var i = 0; i < workerSafety.LlmCalls; i++)
                            parentSafety.RecordCompletedCall(i == 0 ? workerSafety.TurnCumulativeInputTokens : 0, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to generate conversation description for channel '{Channel}' on agent '{Handle}' using model '{Model}'. Keeping provisional description.",
                    channel,
                    config.Handle,
                    workerModel);
                return null;
            }
        }

        private async Task EnsureConversationTrackedAsync(AgentMessage message)
        {
            if (!IsConversationTrackingEnabled())
                return;

            var channel = NormalizeConversationChannel(message.Channel);
            if (channel.Length == 0)
                return;

            var firstSentence = LimitWords(ExtractFirstSentence(message.Message), ConversationDescriptionMaxWords);
            var generate = false;
            var workerInput = message.Message ?? firstSentence;
            try
            {
                await _conversationCatalogLock.WaitAsync();
                try
                {
                    await RetryConversationCatalogWriteAsync();
                    var catalog = await LoadConversationCatalogAsync();
                    if (!catalog.Conversations.TryGetValue(channel, out var entry))
                    {
                        var now = DateTime.UtcNow;
                        entry = new ConversationCatalogEntry
                        {
                            Channel = channel,
                            Description = firstSentence,
                            IsProvisional = true,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now
                        };
                        catalog.Conversations[channel] = entry;
                        await SaveConversationCatalogAsync(catalog);
                    }
                    else if (string.IsNullOrWhiteSpace(entry.Description))
                    {
                        entry.Description = firstSentence;
                        entry.IsProvisional = true;
                        entry.UpdatedAtUtc = DateTime.UtcNow;
                        await SaveConversationCatalogAsync(catalog);
                    }

                    else if (entry.IsProvisional)
                    {
                        workerInput = entry.Description;
                    }

                    generate = entry.IsProvisional && _conversationDescriptionsInFlight.Add(channel);
                }
                finally
                {
                    _conversationCatalogLock.Release();
                }

                if (!generate)
                    return;

                var generated = await TryGenerateConversationDescriptionAsync(channel, workerInput);
                if (string.IsNullOrWhiteSpace(generated))
                    return;

                await _conversationCatalogLock.WaitAsync();
                try
                {
                    // Re-read after the worker call so other channels' updates are retained.
                    await RetryConversationCatalogWriteAsync();
                    var catalog = await LoadConversationCatalogAsync();
                    if (catalog.Conversations.TryGetValue(channel, out var entry) && entry.IsProvisional)
                    {
                        entry.Description = generated;
                        entry.IsProvisional = false;
                        entry.UpdatedAtUtc = DateTime.UtcNow;
                        await SaveConversationCatalogAsync(catalog);
                    }
                }
                finally
                {
                    _conversationCatalogLock.Release();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist conversation metadata for channel '{Channel}' on agent '{Handle}'.", channel, config.Handle);
            }
            finally
            {
                if (generate)
                {
                    await _conversationCatalogLock.WaitAsync();
                    try { _conversationDescriptionsInFlight.Remove(channel); }
                    finally { _conversationCatalogLock.Release(); }
                }
            }
        }

        /// <summary>
        /// Ensures custom state is loaded from persistent storage.
        /// Called lazily on first state access.
        /// </summary>
        private async Task EnsureStateLoadedAsync()
        {
            lock (_stateLock) { if (_customStateLoaded) return; }
            await _stateLoadGate.WaitAsync();
            try
            {
                lock (_stateLock) { if (_customStateLoaded) return; }
                await _stateMergeGate.WaitAsync();
                try
                {
                    var state = new Dictionary<string, JsonElement>(await fabrcoreAgentHost.GetCustomStateAsync());
                    lock (_stateLock) { _customStateCache = state; _customStateLoaded = true; }
                    logger.LogDebug("Loaded custom state with {Count} keys", state.Count);
                }
                finally { _stateMergeGate.Release(); }
            }
            finally { _stateLoadGate.Release(); }
        }

        /// <summary>
        /// Gets a strongly-typed state value by key.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the state to.</typeparam>
        /// <param name="key">The state key.</param>
        /// <returns>The deserialized value, or default if not found.</returns>
        protected async Task<T?> GetStateAsync<T>(string key)
        {
            var result = await TryGetStateAsync<T>(key);
            if (result.Succeeded)
            {
                return result.Value;
            }

            throw CreateStateReadException<T>(result);
        }

        /// <summary>
        /// Attempts to get a strongly-typed state value by key without throwing for unreadable state.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the state to.</typeparam>
        /// <param name="key">The state key.</param>
        /// <returns>A state read result with the value or deserialization diagnostics.</returns>
        protected async Task<StateReadResult<T>> TryGetStateAsync<T>(string key)
        {
            await EnsureStateLoadedAsync();

            lock (_stateLock)
            {
            // Check pending changes first
            if (_pendingStateChanges.TryGetValue(key, out var pendingElement))
            {
                return DeserializeStateElement<T>(key, pendingElement);
            }

            // Check if deleted
            if (_pendingStateDeletes.Contains(key))
            {
                return StateReadSuccess<T>(key, valueKind: null, value: default);
            }

            // Check cache
            if (_customStateCache != null && _customStateCache.TryGetValue(key, out var element))
            {
                return DeserializeStateElement<T>(key, element);
            }

            return StateReadSuccess<T>(key, valueKind: null, value: default);
            }
        }

        private StateReadResult<T> DeserializeStateElement<T>(string key, JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return StateReadSuccess<T>(key, element.ValueKind, default);
            }

            try
            {
                return StateReadSuccess(key, element.ValueKind, element.Deserialize<T>());
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to deserialize custom state key '{Key}' for agent '{Handle}' of type '{AgentType}' as {TargetType}. Stored value kind: {ValueKind}",
                    key,
                    config.Handle,
                    config.AgentType,
                    typeof(T).FullName,
                    element.ValueKind);

                return new StateReadResult<T>
                {
                    Succeeded = false,
                    Key = key,
                    ValueKind = element.ValueKind,
                    Error = ex
                };
            }
        }

        private static StateReadResult<T> StateReadSuccess<T>(string key, JsonValueKind? valueKind, T? value)
        {
            return new StateReadResult<T>
            {
                Succeeded = true,
                Value = value,
                Key = key,
                ValueKind = valueKind
            };
        }

        private InvalidOperationException CreateStateReadException<T>(StateReadResult<T> result)
        {
            var message =
                $"Failed to deserialize custom state key '{result.Key}' for agent '{config.Handle}' " +
                $"of type '{config.AgentType}' as {typeof(T).FullName}. " +
                $"Stored value kind: {result.ValueKind?.ToString() ?? "Missing"}.";

            return new InvalidOperationException(message, result.Error);
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

            lock (_stateLock)
            {
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
            lock (_stateLock)
            {
                _pendingStateChanges[key] = element;
                _pendingStateDeletes.Remove(key);
                _stateVersions[key] = ++_nextStateVersion;
            }
            logger.LogTrace("Set state key: {Key}", key);
        }

        /// <summary>
        /// Removes a state key. Changes are buffered until FlushStateAsync is called.
        /// </summary>
        /// <param name="key">The state key to remove.</param>
        protected void RemoveState(string key)
        {
            lock (_stateLock)
            {
                _pendingStateChanges.Remove(key);
                _pendingStateDeletes.Add(key);
                _stateVersions[key] = ++_nextStateVersion;
            }
            logger.LogTrace("Removed state key: {Key}", key);
        }

        /// <summary>
        /// Returns true if there are unsaved state changes.
        /// </summary>
        protected bool HasPendingStateChanges
        {
            get { lock (_stateLock) return _pendingStateChanges.Count > 0 || _pendingStateDeletes.Count > 0 || _conversationCatalogPendingWrite.HasValue; }
        }

        /// <summary>
        /// Persists all pending state changes to Orleans grain storage.
        /// </summary>
        protected async Task FlushStateAsync()
        {
            Exception? stateError = null;
            await _stateMergeGate.WaitAsync();
            try
            {
                Dictionary<string, JsonElement> changes;
                string[] deletes;
                Dictionary<string, long> versions;
                lock (_stateLock)
                {
                    changes = new(_pendingStateChanges);
                    deletes = _pendingStateDeletes.ToArray();
                    versions = new(_stateVersions);
                }
                if (changes.Count != 0 || deletes.Length != 0)
                {
                    try
                    {
                        await fabrcoreAgentHost.MergeCustomStateAsync(changes, deletes);
                        lock (_stateLock)
                        {
                            if (_customStateCache is not null)
                            {
                                foreach (var key in deletes) _customStateCache.Remove(key);
                                foreach (var (key, value) in changes) _customStateCache[key] = value;
                            }
                            foreach (var (key, version) in versions)
                            {
                                if (_stateVersions.TryGetValue(key, out var current) && current == version)
                                {
                                    _pendingStateChanges.Remove(key);
                                    _pendingStateDeletes.Remove(key);
                                    _stateVersions.Remove(key);
                                }
                            }
                        }
                        logger.LogDebug("Flushed state: {ChangesCount} changes, {DeletesCount} deletes", changes.Count, deletes.Length);
                    }
                    catch (Exception ex) { stateError = ex; }
                }
            }
            finally { _stateMergeGate.Release(); }

            Exception? catalogError = null;
            await _conversationCatalogLock.WaitAsync();
            try { await RetryConversationCatalogWriteAsync(); }
            catch (Exception ex) { catalogError = ex; }
            finally { _conversationCatalogLock.Release(); }
            if (stateError is not null && catalogError is not null) throw new AggregateException(stateError, catalogError);
            if (stateError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stateError).Throw();
            if (catalogError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(catalogError).Throw();
        }

        // Internal methods for IFabrCoreAgentProxy
        bool IFabrCoreAgentProxy.InternalHasPendingStateChanges => HasPendingStateChanges;
        bool IFabrCoreAgentProxy.InternalIsProcessingMessage => LatestTurn() is not null;
        TimeSpan IFabrCoreAgentProxy.InternalProcessingElapsed
        {
            get { var turn = LatestTurn(); return turn is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(turn.Started); }
        }
        bool IFabrCoreAgentProxy.InternalIsProcessingMessageForChannel(string? channel) => FindTurn(channel) is not null;
        TimeSpan IFabrCoreAgentProxy.InternalProcessingElapsedForChannel(string? channel)
        {
            var turn = FindTurn(channel);
            return turn is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(turn.Started);
        }
        AgentMessage? IFabrCoreAgentProxy.InternalActiveMessageForChannel(string? channel) => FindTurn(channel)?.Message;
        string? IFabrCoreAgentProxy.InternalStatusMessageForChannel(string? channel)
        {
            var turn = FindTurn(channel);
            return turn is null ? null : Volatile.Read(ref turn.Status);
        }

        Task IFabrCoreAgentProxy.InternalFlushStateAsync() => FlushStateAsync();

        private readonly List<FabrCoreResolvedToolScope> _configuredToolScopes = [];

        async Task IFabrCoreAgentProxy.InternalDisposeAsync()
        {
            foreach (var scope in _configuredToolScopes.AsEnumerable().Reverse())
            {
                try { await scope.DisposeAsync(); }
                catch (Exception error) { logger.LogWarning(error, "Error disposing configured plugins for agent '{Handle}'", config.Handle); }
            }
            _configuredToolScopes.Clear();
            await DisposeHarnessResourcesAsync();
            await DisposeInternalAgentResourcesAsync();

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

        /// <summary>
        /// Called when a new message arrives while the agent is already processing a message.
        /// The default implementation returns a standard "busy" response.
        /// Override to implement custom busy-state handling (e.g., acknowledge receipt,
        /// reject duplicates, provide status, or perform state-safe read-only work).
        /// <para>
        /// IMPORTANT: Do not mutate shared agent state in this method — the primary OnMessage
        /// may be mid-execution at any await point. The <see cref="ActiveMessage"/> property
        /// returns the message currently being processed by the primary handler.
        /// </para>
        /// </summary>
        /// <param name="message">The incoming message that arrived while busy.</param>
        /// <returns>A response message to send back to the caller.</returns>
        public virtual Task<AgentMessage> OnMessageBusy(AgentMessage message)
        {
            return Task.FromResult(new AgentMessage
            {
                ToHandle = message.FromHandle,
                FromHandle = config.Handle,
                OnBehalfOfHandle = message.OnBehalfOfHandle,
                Message = "Agent is currently processing a message. Please try again shortly.",
                MessageType = message.MessageType,
                Kind = MessageKind.Response,
                TraceId = message.TraceId
            });
        }

        public abstract Task OnInitialize();

        /// <summary>
        /// Called before the agent is reset. Override for custom cleanup.
        /// Default implementation is a no-op.
        /// </summary>
        public virtual Task OnReset()
        {
            return Task.CompletedTask;
        }

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

        async Task IFabrCoreAgentProxy.InternalReset()
        {
            using var activity = ActivitySource.StartActivity("InternalReset", ActivityKind.Internal);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);

            logger.LogInformation("Resetting agent proxy - Handle: {Handle}", config.Handle);

            try
            {
                await OnReset();
                activity?.SetStatus(ActivityStatusCode.Ok);
                logger.LogInformation("Agent proxy reset completed - Handle: {Handle}", config.Handle);
            }
            catch (Exception ex)
            {
                // Log but don't rethrow — reset should proceed even if custom cleanup fails
                logger.LogError(ex, "Error during agent proxy reset - Handle: {Handle}", config.Handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
            }
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

            var startTime = Stopwatch.GetTimestamp();

            var turn = new MessageTurn { Message = message };
            var previousTurn = _currentTurn.Value;
            _currentTurn.Value = turn;
            lock (_turnLock) _activeTurns.Add(turn);
            try
            {
                AgentMessage response;
                using (var llmScope = LlmUsageScope.Begin(
                    agentHandle: fabrcoreAgentHost.GetHandle(),
                    parentMessageId: message.Id,
                    traceId: message.TraceId,
                    originContext: $"OnMessage:{message.Id}"))
                {
                    // Initialize and compact only at an exclusive quiet point. A concurrent turn
                    // skips preflight rather than blocking behind another active handler.
                    if (_historyGate.TryEnterWriter())
                    {
                        try { await EnsureCompactionInitializedAsync(); }
                        finally { _historyGate.ExitWriter(); }
                    }
                    var runSafetyModelConfig = _chatClientConfigName ?? config.Models ?? "default";
                    var runSafetyConfig = _compactionLadder?.RunSafety
                        ?? await BuildRunSafetyConfigAsync(
                            runSafetyModelConfig,
                            _compactionConfig ?? await BuildCompactionConfigAsync(runSafetyModelConfig, _contextCompactionConfig ?? new ContextCompactionConfig()),
                            _contextCompactionConfig ?? await BuildContextCompactionConfigAsync(runSafetyModelConfig));
                    var monitor = serviceProvider.GetService<FabrCore.Core.Monitoring.IAgentMessageMonitor>();
                    using var runSafetyScope = ChatRunSafetyScope.Begin(
                        agentHandle: fabrcoreAgentHost.GetHandle(),
                        parentMessageId: message.Id,
                        traceId: message.TraceId,
                        config: runSafetyConfig,
                        monitor: monitor,
                        logger: logger);

                    try
                    {
                        await EnsureConversationTrackedAsync(message);

                        if (_historyGate.TryEnterWriter())
                        {
                            try { await TryPreflightCompactAsync(); }
                            finally { _historyGate.ExitWriter(); }
                        }

                        await _historyGate.EnterReaderAsync();
                        try { response = await OnMessage(message); }
                        finally { _historyGate.ExitReader(); }

                        // Never wait for another handler here: it may be awaiting this turn.
                        // The last finishing handler performs the shared postflight pass.
                        if (_historyGate.TryEnterWriter())
                        {
                            try { await TryCompactAsync(); }
                            finally { _historyGate.ExitWriter(); }
                        }
                    }
                    catch (FabrCoreRunStoppedException ex)
                    {
                        response = message.Response();
                        response.MessageType = SystemMessageTypes.Error;
                        response.Message = ex.Message;
                        response.Args ??= new Dictionary<string, string>();
                        response.Args["_fabrcore_run_stop_reason"] = ex.Reason.ToString();
                        response.Args["_actual_prompt_input_tokens"] = ex.ActualPromptInputTokens.ToString();
                        response.Args["_turn_cumulative_input_tokens"] = ex.TurnCumulativeInputTokens.ToString();
                        response.Args["_fabrcore_llm_calls"] = ex.LlmCalls.ToString();
                    }

                    // Attach LLM usage metrics to the response
                    if (llmScope.CallCount > 0)
                    {
                        response.Args ??= new Dictionary<string, string>();
                        llmScope.ApplyTo(response.Args);
                    }

                    response.Args ??= new Dictionary<string, string>();
                    runSafetyScope.ApplyTo(response.Args);
                    if (!response.Args.ContainsKey("_llm_calls") && (runSafetyScope.LlmCalls > 0 || runSafetyScope.StopReason != RunStopReason.None))
                        response.Args["_llm_calls"] = runSafetyScope.LlmCalls.ToString();
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
            finally
            {
                lock (_turnLock) _activeTurns.Remove(turn);
                _currentTurn.Value = previousTurn;
            }
        }

        async Task<AgentMessage> IFabrCoreAgentProxy.InternalOnMessageBusy(AgentMessage message)
        {
            using var activity = ActivitySource.StartActivity("InternalOnMessageBusy", ActivityKind.Server);
            activity?.SetTag("agent.type", config.AgentType);
            activity?.SetTag("agent.handle", config.Handle);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);
            activity?.SetTag("message.route", "busy");

            logger.LogDebug("Agent proxy busy — routing to OnMessageBusy - From: {FromHandle}, To: {ToHandle}",
                message.FromHandle, message.ToHandle);

            var startTime = Stopwatch.GetTimestamp();
            var previousTurn = _currentTurn.Value;
            var primary = IsChannelBusyRoutingEnabled() ? FindTurn(message.Channel) : LatestTurn();
            _currentTurn.Value = new MessageTurn { Message = primary?.Message };
            try
            {
                var response = await OnMessageBusy(message);

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                MessageProcessingDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("agent.type", config.AgentType),
                    new KeyValuePair<string, object?>("agent.handle", config.Handle),
                    new KeyValuePair<string, object?>("message.route", "busy"));

                BusyMessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("agent.type", config.AgentType),
                    new KeyValuePair<string, object?>("agent.handle", config.Handle));

                activity?.SetStatus(ActivityStatusCode.Ok);
                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing busy message in agent proxy - From: {FromHandle}, To: {ToHandle}",
                    message.FromHandle, message.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "busy_message_processing_failed"),
                    new KeyValuePair<string, object?>("agent.type", config.AgentType));
                throw;
            }
            finally { _currentTurn.Value = previousTurn; }
        }

        private bool IsChannelBusyRoutingEnabled() => config.Args is { } args
            && args.TryGetValue("_ChannelBusyRoutingEnabled", out var raw)
            && bool.TryParse(raw, out var enabled) && enabled;

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
