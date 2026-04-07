using FabrCore.Core;
using FabrCore.Sdk.Agent;
using FabrCore.Sdk.Chat;
using FabrCore.Sdk.Compaction;
using FabrCore.Sdk.History;
using FabrCore.Sdk.Mcp;
using FabrCore.Sdk.Providers;
using FabrCore.Sdk.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace FabrCore.Sdk;

/// <summary>
/// Abstract base class for FabrCore agent proxy implementations.
/// Provides config, LLM client creation, tool resolution, MCP, compaction, and custom state.
/// Clients extend this class and override OnInitialize/OnMessage/OnEvent/OnReset.
/// </summary>
public abstract class FabrCoreAgentProxy : IFabrCoreAgentProxy
{
    private static readonly ActivitySource ActivitySource = new("FabrCore.Sdk.AgentProxy");
    private static readonly Meter Meter = new("FabrCore.Sdk.AgentProxy");

    private static readonly Counter<long> AgentInitializedCounter = Meter.CreateCounter<long>(
        "fabrcore.agent.proxy.initialized", description: "Number of agent proxies initialized");
    private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
        "fabrcore.agent.proxy.messages.processed", description: "Number of messages processed");
    private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
        "fabrcore.agent.proxy.message.duration", unit: "ms", description: "Duration of message processing");
    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
        "fabrcore.agent.proxy.errors", description: "Number of errors");
    private static readonly Counter<long> McpServersConnectedCounter = Meter.CreateCounter<long>(
        "fabrcore.agent.proxy.mcp.servers.connected", description: "MCP servers connected");
    private static readonly Counter<long> McpErrorsCounter = Meter.CreateCounter<long>(
        "fabrcore.agent.proxy.mcp.errors", description: "MCP connection errors");
    private static readonly Counter<long> McpServersDisposedCounter = Meter.CreateCounter<long>(
        "fabrcore.agent.proxy.mcp.servers.disposed", description: "MCP servers disposed");

    protected readonly AgentConfiguration config;
    protected readonly IFabrCoreAgentHost fabrcoreAgentHost;
    protected readonly IServiceProvider serviceProvider;
    protected readonly ILoggerFactory loggerFactory;
    protected readonly ILogger<FabrCoreAgentProxy> logger;
    protected readonly IConfiguration configuration;
    protected readonly IFabrCoreChatClientService chatClientService;

    private DateTime? _initializedAt;
    private AgentMessage? _activeMessage;

    /// <summary>The message currently being processed.</summary>
    protected AgentMessage? ActiveMessage => _activeMessage;

    private volatile string? _statusMessage;

    /// <summary>Sets the status message shown in the heartbeat loop.</summary>
    protected void SetStatusMessage(string? message) => _statusMessage = message;

    string? IFabrCoreAgentProxy.StatusMessage
    {
        get => _statusMessage;
        set => _statusMessage = value;
    }

    // MCP client lifecycle tracking
    private readonly List<McpClient> _mcpClients = new();

    // Compaction plumbing
    private FabrCoreChatHistoryProvider? _chatHistoryProvider;
    private string? _chatClientConfigName;
    private CompactionService? _compactionService;

    /// <summary>The lazily-resolved CompactionService instance.</summary>
    protected CompactionService? CompactionServiceInstance => _compactionService;
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

        this.loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        this.configuration = serviceProvider.GetRequiredService<IConfiguration>();
        this.chatClientService = serviceProvider.GetRequiredService<IFabrCoreChatClientService>();

        logger = loggerFactory.CreateLogger<FabrCoreAgentProxy>();
        logger.LogDebug("FabrCoreAgentProxy created - AgentType: {AgentType}, Handle: {Handle}", config.AgentType, config.Handle);
    }

    /// <summary>Gets an IFabrCoreChatClient wrapped with token tracking.</summary>
    protected async Task<IFabrCoreChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
    {
        var client = await chatClientService.GetChatClient(name, networkTimeoutSeconds);
        return new TokenTrackingChatClient(client);
    }

    /// <summary>
    /// Creates a FabrCoreAgent with standard configuration.
    /// Chat messages are automatically persisted to Orleans grain state.
    /// </summary>
    protected async Task<FabrCoreAgentResult> CreateChatClientAgent(
        string chatClientConfigName,
        string threadId,
        IList<ChatTool>? tools = null,
        Action<FabrCoreAgentOptions>? configureOptions = null)
    {
        var chatClient = await GetChatClient(chatClientConfigName);
        var historyProvider = FabrCoreChatHistoryProvider.Create(fabrcoreAgentHost, threadId, logger);

        var options = new FabrCoreAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = config.SystemPrompt,
                Tools = tools
            },
            Name = fabrcoreAgentHost.GetHandle(),
            ChatHistoryProvider = historyProvider
        };

        configureOptions?.Invoke(options);

        var agent = new FabrCoreAgent(chatClient, options, logger);
        var session = agent.CreateSession(threadId);

        _chatHistoryProvider = historyProvider;
        _chatClientConfigName = chatClientConfigName;

        logger.LogDebug("Created FabrCoreAgent - Config: {Config}, ThreadId: {ThreadId}", chatClientConfigName, threadId);

        return new FabrCoreAgentResult(agent, session, historyProvider);
    }

    /// <summary>Resolves all configured tools (plugins + standalone + MCP).</summary>
    protected async Task<List<ChatTool>> ResolveConfiguredToolsAsync()
    {
        var registry = serviceProvider.GetRequiredService<FabrCoreToolRegistry>();
        var tools = await registry.ResolveToolsAsync(serviceProvider, config.Plugins, config.Tools, config, fabrcoreAgentHost);

        if (config.McpServers is { Count: > 0 })
        {
            foreach (var mcpConfig in config.McpServers)
            {
                try
                {
                    var mcpTools = await ConnectMcpServerAsync(mcpConfig);
                    tools.AddRange(mcpTools);
                    logger.LogInformation("MCP server '{Name}' provided {ToolCount} tools", mcpConfig.Name ?? "(unnamed)", mcpTools.Count);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to connect MCP server '{Name}' — agent will continue without its tools", mcpConfig.Name ?? "(unnamed)");
                    McpErrorsCounter.Add(1,
                        new KeyValuePair<string, object?>("agent.handle", config.Handle),
                        new KeyValuePair<string, object?>("mcp.server", mcpConfig.Name));
                }
            }
        }

        logger.LogInformation("Agent '{Handle}' resolved {ToolCount} total tools: [{ToolNames}]",
            config.Handle, tools.Count, string.Join(", ", tools.Select(t => t.Name)));

        return tools;
    }

    /// <summary>Connects to an MCP server and returns its tools.</summary>
    protected async Task<IList<ChatTool>> ConnectMcpServerAsync(McpServerConfig mcpConfig)
    {
        using var activity = ActivitySource.StartActivity("ConnectMcpServerAsync", ActivityKind.Client);
        activity?.SetTag("mcp.server.name", mcpConfig.Name);
        activity?.SetTag("mcp.transport", mcpConfig.TransportType.ToString());

        logger.LogInformation("Connecting to MCP server '{Name}' via {Transport}", mcpConfig.Name ?? "(unnamed)", mcpConfig.TransportType);

        IClientTransport transport = mcpConfig.TransportType switch
        {
            McpTransportType.Stdio => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = mcpConfig.Name,
                Command = mcpConfig.Command ?? throw new ArgumentException($"MCP server '{mcpConfig.Name}' requires Command for Stdio transport"),
                Arguments = mcpConfig.Arguments,
                EnvironmentVariables = mcpConfig.Env?.Count > 0 ? mcpConfig.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value) : null
            }, loggerFactory),
            McpTransportType.Http => new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = mcpConfig.Name,
                Endpoint = new Uri(mcpConfig.Url ?? throw new ArgumentException($"MCP server '{mcpConfig.Name}' requires Url for Http transport")),
                AdditionalHeaders = mcpConfig.Headers?.Count > 0 ? mcpConfig.Headers.ToDictionary(kv => kv.Key, kv => kv.Value) : null
            }, loggerFactory),
            _ => throw new ArgumentException($"Unsupported MCP transport type: {mcpConfig.TransportType}")
        };

        var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory);
        _mcpClients.Add(client);

        var tools = await McpToolAdapter.GetToolsFromClientAsync(client);

        McpServersConnectedCounter.Add(1,
            new KeyValuePair<string, object?>("agent.handle", config.Handle),
            new KeyValuePair<string, object?>("mcp.server", mcpConfig.Name));

        logger.LogInformation("Connected to MCP server '{Name}' — {ToolCount} tools: [{ToolNames}]",
            mcpConfig.Name ?? "(unnamed)", tools.Count, string.Join(", ", tools.Select(t => t.Name)));

        activity?.SetTag("mcp.tools.count", tools.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return tools;
    }

    #region Compaction

    public virtual async Task<CompactionResult?> OnCompaction(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        CompactionConfig compactionConfig,
        int estimatedTokens = 0)
    {
        if (_compactionService is null || _chatClientConfigName is null)
            return null;

        _statusMessage = "Compacting..";
        try
        {
            return await _compactionService.CompactIfNeededAsync(chatHistoryProvider, compactionConfig);
        }
        finally
        {
            _statusMessage = null;
        }
    }

    #endregion

    #region Custom State

    /// <summary>Gets a strongly-typed state value by key.</summary>
    protected async Task<T?> GetStateAsync<T>(string key)
    {
        await EnsureCustomStateLoadedAsync();
        if (_customStateCache!.TryGetValue(key, out var element))
            return element.Deserialize<T>();
        return default;
    }

    /// <summary>Sets a strongly-typed state value. Changes are buffered until FlushStateAsync is called.</summary>
    protected void SetState<T>(string key, T value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        _pendingStateChanges[key] = element;
        _customStateCache ??= new Dictionary<string, JsonElement>();
        _customStateCache[key] = element;
    }

    /// <summary>Removes a state key. Changes are buffered until FlushStateAsync is called.</summary>
    protected void DeleteState(string key)
    {
        _pendingStateDeletes.Add(key);
        _pendingStateChanges.Remove(key);
        _customStateCache?.Remove(key);
    }

    /// <summary>Persists all pending state changes to Orleans grain state.</summary>
    protected async Task FlushStateAsync()
    {
        if (_pendingStateChanges.Count == 0 && _pendingStateDeletes.Count == 0)
            return;

        var changes = new Dictionary<string, JsonElement>(_pendingStateChanges);
        var deletes = _pendingStateDeletes.ToList();
        _pendingStateChanges.Clear();
        _pendingStateDeletes.Clear();
        await fabrcoreAgentHost.MergeCustomStateAsync(changes, deletes);
    }

    private async Task EnsureCustomStateLoadedAsync()
    {
        if (_customStateLoaded) return;
        _customStateCache = await fabrcoreAgentHost.GetCustomStateAsync();
        _customStateLoaded = true;
    }

    #endregion

    #region IFabrCoreAgentProxy Internal Implementation

    async Task IFabrCoreAgentProxy.InternalInitialize()
    {
        _initializedAt = DateTime.UtcNow;
        await OnInitialize();
        AgentInitializedCounter.Add(1, new KeyValuePair<string, object?>("agent.type", config.AgentType));
    }

    async Task<AgentMessage> IFabrCoreAgentProxy.InternalOnMessage(AgentMessage message)
    {
        var sw = Stopwatch.StartNew();
        _activeMessage = message;
        try
        {
            var result = await OnMessage(message);
            MessagesProcessedCounter.Add(1, new KeyValuePair<string, object?>("agent.type", config.AgentType));
            return result;
        }
        catch (Exception ex)
        {
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("agent.type", config.AgentType),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            throw;
        }
        finally
        {
            _activeMessage = null;
            sw.Stop();
            MessageProcessingDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("agent.type", config.AgentType));
        }
    }

    async Task IFabrCoreAgentProxy.InternalOnEvent(EventMessage message)
    {
        await OnEvent(message);
    }

    async Task<ProxyHealthStatus> IFabrCoreAgentProxy.InternalGetHealth(HealthDetailLevel detailLevel)
    {
        return await GetHealth(detailLevel);
    }

    async Task IFabrCoreAgentProxy.InternalReset()
    {
        await OnReset();
    }

    async Task IFabrCoreAgentProxy.InternalFlushStateAsync()
    {
        await FlushStateAsync();
    }

    async Task IFabrCoreAgentProxy.InternalDisposeAsync()
    {
        foreach (var client in _mcpClients)
        {
            try
            {
                await client.DisposeAsync();
                McpServersDisposedCounter.Add(1);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing MCP client");
            }
        }
        _mcpClients.Clear();
    }

    bool IFabrCoreAgentProxy.InternalHasPendingStateChanges =>
        _pendingStateChanges.Count > 0 || _pendingStateDeletes.Count > 0;

    #endregion

    #region Virtual Methods

    public virtual Task OnInitialize() => Task.CompletedTask;
    public abstract Task<AgentMessage> OnMessage(AgentMessage message);
    public virtual Task OnReset() => Task.CompletedTask;
    public virtual Task OnEvent(EventMessage message) => Task.CompletedTask;

    public virtual Task<ProxyHealthStatus> GetHealth(HealthDetailLevel detailLevel)
    {
        return Task.FromResult(new ProxyHealthStatus
        {
            State = HealthState.Healthy,
            IsInitialized = _initializedAt.HasValue,
            ProxyTypeName = GetType().Name,
            InitializedAt = _initializedAt,
            CustomMetrics = new Dictionary<string, string>
            {
                ["mcpClients"] = _mcpClients.Count.ToString(),
                ["hasPendingState"] = (_pendingStateChanges.Count > 0 || _pendingStateDeletes.Count > 0).ToString()
            }
        });
    }

    #endregion

    #region Response Helpers

    /// <summary>Creates a response message routed back to the sender.</summary>
    protected AgentMessage Response(string message, string? type = null)
    {
        if (_activeMessage == null)
            throw new InvalidOperationException("Response() can only be called during OnMessage processing");

        return new AgentMessage
        {
            ToHandle = _activeMessage.DeliverToHandle ?? _activeMessage.FromHandle,
            FromHandle = config.Handle,
            OnBehalfOfHandle = _activeMessage.OnBehalfOfHandle,
            Message = message,
            MessageType = type ?? _activeMessage.MessageType,
            Kind = MessageKind.Response
        };
    }

    #endregion
}
