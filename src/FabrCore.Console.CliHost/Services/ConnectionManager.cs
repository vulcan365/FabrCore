using FabrCore.Client;
using FabrCore.Core;
using Microsoft.Extensions.Logging;

namespace FabrCore.Console.CliHost.Services;

public class ConnectionManager : IConnectionManager
{
    private readonly IClientContextFactory _contextFactory;
    private readonly IFabrCoreHostApiClient _apiClient;
    private readonly CliOptions _options;
    private readonly ILogger<ConnectionManager> _logger;

    private IClientContext? _context;
    private TaskCompletionSource<AgentMessage>? _pendingResponse;

    public bool IsConnectedToAgent => CurrentAgentHandle != null;
    public string CurrentHandle => _options.Handle;
    public string? CurrentAgentHandle { get; private set; }

    public event Action<string>? ThinkingReceived;

    public ConnectionManager(
        IClientContextFactory contextFactory,
        IFabrCoreHostApiClient apiClient,
        CliOptions options,
        ILogger<ConnectionManager> logger)
    {
        _contextFactory = contextFactory;
        _apiClient = apiClient;
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _context = await _contextFactory.GetOrCreateAsync(_options.Handle, ct);
        _context.AgentMessageReceived += OnAgentMessageReceived;
        _logger.LogInformation("Connection initialized for handle: {Handle}", _options.Handle);
    }

    public void ConnectToAgent(string handle)
    {
        CurrentAgentHandle = handle;
        _logger.LogInformation("Connected to agent: {Handle}", handle);
    }

    public async Task<AgentHealthStatus> CreateAgentAsync(string agentType, string? handle = null, CancellationToken ct = default)
    {
        EnsureInitialized();

        handle ??= agentType.ToLowerInvariant();

        var config = new AgentConfiguration
        {
            Handle = handle,
            AgentType = agentType,
            Args = new Dictionary<string, string> { { "userId", _options.Handle } }
        };

        var health = await _context!.CreateAgent(config);

        // Auto-connect on successful creation
        CurrentAgentHandle = handle;
        _logger.LogInformation("Created and connected to agent: {Handle} ({Type})", handle, agentType);

        return health;
    }

    public async Task<AgentMessage> SendMessageAsync(string message, CancellationToken ct = default)
    {
        EnsureInitialized();

        if (!IsConnectedToAgent)
            throw new InvalidOperationException("Not connected to any agent. Use /connect or /create first.");

        var agentHandle = CurrentAgentHandle!;

        // Build the message following the ChatDock pattern
        var request = new AgentMessage
        {
            FromHandle = _options.Handle,
            ToHandle = agentHandle,
            Message = message,
            MessageType = "chat"
        };

        // Set up response waiting
        var tcs = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponse = tcs;

        try
        {
            // Fire-and-forget via Orleans stream
            await _context!.SendMessage(request);

            // Wait for the response with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            try
            {
                return await tcs.Task;
            }
            finally
            {
                await registration.DisposeAsync();
            }
        }
        finally
        {
            _pendingResponse = null;
        }
    }

    public async Task<AgentsListResponse> GetAgentsAsync(string? status = null, CancellationToken ct = default)
    {
        return await _apiClient.GetAgentsAsync(status, ct);
    }

    public async Task<List<TrackedAgentInfo>> GetTrackedAgentsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        return await _context!.GetTrackedAgents();
    }

    public async Task<List<AgentCreationResult>> CreateAgentsAsync(List<AgentConfiguration> agents, CancellationToken ct = default)
    {
        EnsureInitialized();

        var results = new List<AgentCreationResult>();

        foreach (var agentConfig in agents)
        {
            var handle = agentConfig.Handle ?? agentConfig.AgentType?.ToLowerInvariant() ?? "unknown";
            var agentType = agentConfig.AgentType ?? "unknown";

            try
            {
                // Inject userId if not already present
                if (!agentConfig.Args.ContainsKey("userId"))
                    agentConfig.Args["userId"] = _options.Handle;

                await _context!.CreateAgent(agentConfig);
                results.Add(new AgentCreationResult(handle, agentType, true));
                _logger.LogInformation("Batch-created agent: {Handle} ({Type})", handle, agentType);
            }
            catch (Exception ex)
            {
                results.Add(new AgentCreationResult(handle, agentType, false, ex.Message));
                _logger.LogWarning(ex, "Failed to create agent: {Handle} ({Type})", handle, agentType);
            }
        }

        // Auto-connect to first successfully created agent
        var firstSuccess = results.FirstOrDefault(r => r.Success);
        if (firstSuccess != null)
        {
            CurrentAgentHandle = firstSuccess.Handle;
            _logger.LogInformation("Auto-connected to first created agent: {Handle}", firstSuccess.Handle);
        }

        return results;
    }

    public async Task<AgentHealthStatus> GetHealthAsync(string? handle = null, CancellationToken ct = default)
    {
        EnsureInitialized();
        handle ??= CurrentAgentHandle ?? throw new InvalidOperationException("No agent specified and not connected to any agent.");
        return await _context!.GetAgentHealth(handle, HealthDetailLevel.Detailed);
    }

    private void OnAgentMessageReceived(object? sender, AgentMessage message)
    {
        if (CurrentAgentHandle == null) return;

        // Filter: only accept messages from the connected agent to our handle
        // Follow the ChatDock pattern: the agent's full handle may be prefixed
        var expectedFrom = CurrentAgentHandle.Contains(':')
            ? CurrentAgentHandle
            : $"{_options.Handle}:{CurrentAgentHandle}";

        if (message.FromHandle != expectedFrom || message.ToHandle != _options.Handle)
        {
            _logger.LogDebug("Ignoring message from {From} to {To} (expected from {Expected})",
                message.FromHandle, message.ToHandle, expectedFrom);
            return;
        }

        if (message.MessageType == "thinking")
        {
            ThinkingReceived?.Invoke(message.Message ?? "Thinking...");
            return;
        }

        // Final response - complete the pending wait
        _pendingResponse?.TrySetResult(message);
    }

    private void EnsureInitialized()
    {
        if (_context == null)
            throw new InvalidOperationException("ConnectionManager has not been initialized. Call InitializeAsync first.");
    }
}
