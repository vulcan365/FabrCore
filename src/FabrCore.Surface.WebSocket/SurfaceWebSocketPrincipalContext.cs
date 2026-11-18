using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Client.WebSocket;
using FabrCore.Core;
using Microsoft.Extensions.Logging;

namespace FabrCore.Surface.WebSocket;

/// <summary>
/// A principal's connection to a FabrCore host over <c>/ws</c>, for Surface's chat. Answers arrive as deliveries, however long an agent
/// takes, rather than through a request that a proxy cuts off. It also:
/// <list type="bullet">
/// <item>reads and acknowledges deliveries, and keeps reading across the client's own reconnects;</item>
/// <item>creates agents over the host's REST API (the WebSocket has no create), and creates them again when a restarted host
/// reports them not set up (<see cref="SurfaceWebSocketOptions.RestoreCreatedAgents"/>);</item>
/// <item>stops a chat waiting when an agent says nothing for <see cref="SurfaceWebSocketOptions.QuietLimit"/>, by delivering an error
/// from that agent;</item>
/// <item>throws <see cref="SurfaceConnectionException"/> saying which step failed.</item>
/// </list>
/// </summary>
public sealed class SurfaceWebSocketPrincipalContext : ISurfacePrincipalContext
{
    /// <summary>Set on the error delivered when an agent said nothing for the quiet limit.</summary>
    public const string NoAnswerArg = "_fabrcore_surface_no_answer";

    private const string RunStopReasonArg = "_fabrcore_run_stop_reason";

    private readonly HttpClient http;
    private readonly SurfaceWebSocketOptions options;
    private readonly Func<HttpClient, FabrCoreWebSocketClientOptions, IFabrCoreWebSocketClient> openSocket;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly string handlePrefix;
    private readonly SemaphoreSlim connecting = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<string, AgentConfiguration> created = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock eventLock = new();
    private EventHandler<AgentMessage>? agentMessageReceived;
    private IFabrCoreWebSocketClient? socket;
    private Task? reading;
    private ITimer? quietCheck;
    private volatile bool disposed;

    internal SurfaceWebSocketPrincipalContext(
        string handle,
        HttpClient http,
        SurfaceWebSocketOptions options,
        Func<HttpClient, FabrCoreWebSocketClientOptions, IFabrCoreWebSocketClient> openSocket,
        ILogger logger,
        TimeProvider time)
    {
        Handle = handle;
        handlePrefix = HandleUtilities.BuildPrefix(handle);
        this.http = http;
        this.options = options;
        this.openSocket = openSocket;
        this.logger = logger;
        this.time = time;
    }

    public string Handle { get; }

    public bool IsDisposed => disposed;

    public event EventHandler<AgentMessage>? AgentMessageReceived
    {
        add
        {
            lock (eventLock)
                agentMessageReceived += value;
        }
        remove
        {
            lock (eventLock)
                agentMessageReceived -= value;
        }
    }

    /// <summary>Opens the connection, or says why it can't be opened. Every call below opens it too, if it isn't open.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
        => await EnsureConnectedAsync(cancellationToken);

    public async Task SendMessage(AgentMessage request)
    {
        request.FromHandle ??= Handle;
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        var target = Qualify(request.ToHandle);
        if (request.Kind != MessageKind.OneWay && target is not null)
            waiting[target] = time.GetUtcNow().UtcTicks;
        try
        {
            await connection.SendMessageAsync(request);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            if (target is not null)
                waiting.TryRemove(target, out _);
            logger.LogWarning(ex, "Sending to {Agent} over the FabrCore WebSocket failed.", request.ToHandle);
            throw new SurfaceConnectionException(SurfaceConnectionProblem.Unreachable,
                "That didn't reach the FabrCore host. Try sending it again.", ex);
        }
    }

    public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
    {
        request.FromHandle ??= Handle;
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        return await Guard(() => connection.SendMessageAndReceiveAsync(request), "send to the agent");
    }

    public async Task SendEvent(EventMessage request)
    {
        request.Source ??= Handle;
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        await Guard(() => connection.SendEventAsync(request), "send the event");
    }

    /// <summary>Creates the agent over the host's REST API, as this principal, and remembers it so it can be restored.</summary>
    public async Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
    {
        ArgumentNullException.ThrowIfNull(agentConfiguration);
        ThrowIfDisposed();
        await CreateOnHostAsync(agentConfiguration, CancellationToken.None);
        var handle = Qualify(agentConfiguration.Handle) ?? agentConfiguration.Handle!;
        created[handle] = agentConfiguration;
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        return await Guard(() => connection.GetAgentHealthAsync(handle, HealthDetailLevel.Basic), "check the agent");
    }

    public async Task<AgentHealthStatus> ResetAgent(string handle)
    {
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        if (Qualify(handle) is { } target)
            waiting.TryRemove(target, out _);
        return await Guard(() => connection.ResetAgentAsync(handle), "reset the agent");
    }

    /// <summary>
    /// The agent's health. An agent this context created that a restarted host lists but hasn't set up is created again first, so a
    /// health check before a message finds it ready.
    /// </summary>
    public async Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
    {
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        var health = await Guard(() => connection.GetAgentHealthAsync(handle, detailLevel), "check the agent");
        if (health.IsConfigured || !options.RestoreCreatedAgents || Qualify(handle) is not { } qualified
            || !created.TryGetValue(qualified, out var configuration))
            return health;

        logger.LogInformation("The FabrCore host has {Agent} but hasn't set it up; creating it again.", qualified);
        await CreateOnHostAsync(configuration, CancellationToken.None);
        return await Guard(() => connection.GetAgentHealthAsync(handle, detailLevel), "check the agent");
    }

    public async Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false)
    {
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        return [.. await Guard(() => connection.GetTrackedAgentsAsync(activate), "list your agents")];
    }

    public async Task<bool> IsAgentTracked(string handle)
    {
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        return await Guard(() => connection.IsAgentTrackedAsync(handle), "check the agent");
    }

    public async Task<List<AgentInfo>> GetAccessibleSharedAgents()
    {
        var connection = await EnsureConnectedAsync(CancellationToken.None);
        return [.. await Guard(() => connection.GetSharedAgentsAsync(), "list shared agents")];
    }

    private async Task<IFabrCoreWebSocketClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (socket is { } open)
            return open;

        await connecting.WaitAsync(cancellationToken);
        try
        {
            if (socket is { } opened)
                return opened;
            if (options.HostUrl is not { } host)
                throw new SurfaceConnectionException(SurfaceConnectionProblem.NotConfigured,
                    "This app isn't connected to a FabrCore host yet.");

            var next = openSocket(http, new FabrCoreWebSocketClientOptions { HostUri = host, ClientId = Handle });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
            timeout.CancelAfter(options.ConnectTimeout);
            try
            {
                await next.ConnectAsync(timeout.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                await next.DisposeAsync();
                throw ConnectProblem(ex);
            }

            socket = next;
            reading = Task.Run(() => ReadAsync(next, stopping.Token));
            var period = TimeSpan.FromTicks(Math.Clamp(options.QuietLimit.Ticks / 6, TimeSpan.FromMilliseconds(20).Ticks, TimeSpan.FromSeconds(15).Ticks));
            quietCheck ??= time.CreateTimer(_ => CheckQuiet(), null, period, period);
            return next;
        }
        finally
        {
            connecting.Release();
        }
    }

    private SurfaceConnectionException ConnectProblem(Exception ex)
    {
        logger.LogWarning(ex, "Opening the FabrCore WebSocket for {Principal} failed.", Handle);
        return ex switch
        {
            OperationCanceledException => new(SurfaceConnectionProblem.Unreachable,
                "The FabrCore host didn't open a connection in time. Check that it's running, then try again.", ex),
            FabrCoreWebSocketException { Retryable: false } => new(SurfaceConnectionProblem.Refused,
                "The FabrCore host refused this app's connection. Check the app's key for the host.", ex),
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => new(SurfaceConnectionProblem.Refused,
                "The FabrCore host refused this app's connection. Check the app's key for the host.", ex),
            _ => new(SurfaceConnectionProblem.Unreachable,
                "The FabrCore host couldn't be reached just now. Check that it's running, then try again.", ex)
        };
    }

    /// <summary>Creates an agent with <c>POST fabrcoreapi/Agent/create</c>, which refuses a type the host doesn't have.</summary>
    private async Task CreateOnHostAsync(AgentConfiguration configuration, CancellationToken cancellationToken)
    {
        if (options.HostUrl is not { } host)
            throw new SurfaceConnectionException(SurfaceConnectionProblem.NotConfigured, "This app isn't connected to a FabrCore host yet.");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(new Uri(host, "fabrcoreapi/Agent/create"), new[] { configuration }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Creating {Agent} on the FabrCore host failed.", configuration.Handle);
            throw new SurfaceConnectionException(SurfaceConnectionProblem.Unreachable,
                "The FabrCore host couldn't be reached just now. Check that it's running, then try again.", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SurfaceConnectionException(SurfaceConnectionProblem.Refused,
                    "The FabrCore host refused this app's key, so it couldn't set up your agent.");
            if (!response.IsSuccessStatusCode)
                throw new SurfaceConnectionException(SurfaceConnectionProblem.Unreachable,
                    $"The FabrCore host couldn't set up your agent ({(int)response.StatusCode}). Try again in a moment.");

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (CreateSucceeded(body))
                return;
            logger.LogWarning("The FabrCore host couldn't create {AgentType}: {Result}", configuration.AgentType, body);
            throw new SurfaceConnectionException(SurfaceConnectionProblem.AgentMissing,
                $"The FabrCore host doesn't have the agent type '{configuration.AgentType}', so it couldn't set up your agent.");
        }
    }

    internal static bool CreateSucceeded(string body)
    {
        try
        {
            using var result = JsonDocument.Parse(body);
            return result.RootElement.ValueKind == JsonValueKind.Object
                   && result.RootElement.TryGetProperty("successCount", out var count)
                   && count.TryGetInt32(out var n) && n > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<T> Guard<T>(Func<Task<T>> call, string what)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            logger.LogWarning(ex, "The FabrCore WebSocket couldn't {What}.", what);
            throw new SurfaceConnectionException(SurfaceConnectionProblem.Unreachable,
                $"The FabrCore host couldn't be reached to {what}. Try again in a moment.", ex);
        }
        catch (FabrCoreWebSocketException ex)
        {
            // Not retryable: the host answered and said no, such as an access grant the principal doesn't have.
            logger.LogWarning(ex, "The FabrCore host wouldn't {What} ({Code}).", what, ex.Code);
            throw new SurfaceConnectionException(SurfaceConnectionProblem.Refused,
                $"The FabrCore host wouldn't let you {what}: {ex.Message}", ex);
        }
    }

    private static bool IsConnectionFailure(Exception ex)
        => ex is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException
           || ex is FabrCoreWebSocketException { Retryable: true };

    /// <summary>Answers arrive as deliveries, which the client keeps across its own reconnects.</summary>
    private async Task ReadAsync(IFabrCoreWebSocketClient connection, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var delivery in connection.ReadDeliveriesAsync(cancellationToken))
            {
                Receive(delivery.Message);
                await connection.AcknowledgeAsync(delivery.Sequence, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the FabrCore WebSocket for {Principal} stopped.", Handle);
        }

        // A connection the context already replaced says nothing about the context now. Otherwise open a new one on the next call.
        if (ReferenceEquals(socket, connection) && !disposed)
        {
            socket = null;
            await connection.DisposeAsync();
        }
    }

    internal void Receive(AgentMessage message)
    {
        if (disposed)
            return;

        if (Qualify(message.FromHandle) is { } from && waiting.ContainsKey(from))
        {
            if (message.MessageType is SystemMessageTypes.Status or SystemMessageTypes.Thinking)
                waiting[from] = time.GetUtcNow().UtcTicks;
            else if (!SystemMessageTypes.IsSystemMessage(message.MessageType) || message.MessageType == SystemMessageTypes.Error)
                waiting.TryRemove(from, out _);
        }

        if (message.MessageType == SystemMessageTypes.Error)
            message.Message = ErrorText(message);
        Raise(message);
    }

    /// <summary>Delivers an error from each agent that has said nothing for the quiet limit, which ends the chat's wait for it.</summary>
    internal void CheckQuiet()
    {
        var now = time.GetUtcNow().UtcTicks;
        foreach (var (agent, heard) in waiting)
        {
            if (now - heard < options.QuietLimit.Ticks || !waiting.TryRemove(new KeyValuePair<string, long>(agent, heard)))
                continue;
            logger.LogWarning("{Agent} said nothing for {Limit}; the chat stopped waiting.", agent, options.QuietLimit);
            Raise(new AgentMessage
            {
                FromHandle = agent,
                ToHandle = Handle,
                MessageType = SystemMessageTypes.Error,
                Kind = MessageKind.OneWay,
                Message = options.NoAnswerMessage,
                Args = new Dictionary<string, string> { [NoAnswerArg] = "true" }
            });
        }
    }

    /// <summary>
    /// What went wrong, as the host said it. FabrCore stops a turn whose prompt outgrows the model (or its budget); that's the
    /// conversation, not the message, so the way out is a new one.
    /// </summary>
    internal static string ErrorText(AgentMessage message)
    {
        var detail = message.Message?.Trim() ?? string.Empty;
        var stopped = message.Args is { } args && args.TryGetValue(RunStopReasonArg, out var reason) ? reason : null;
        if (message.Args?.ContainsKey(NoAnswerArg) == true)
            return detail;
        if (stopped is "PromptTooLarge" or "TurnBudgetExceeded"
            || detail.Contains("PromptTooLarge", StringComparison.Ordinal) || detail.Contains("TurnBudgetExceeded", StringComparison.Ordinal))
            return "This conversation has grown too long for the AI model to take in. Start a new conversation and ask again.";
        return detail.Length == 0
            ? "Something went wrong while answering. Try asking again, or start a new conversation."
            : $"Something went wrong while answering: {(detail.Length > 300 ? detail[..300] + "…" : detail)} Try asking again, or start a new conversation.";
    }

    private void Raise(AgentMessage message)
    {
        EventHandler<AgentMessage>? handlers;
        lock (eventLock)
            handlers = agentMessageReceived;
        try
        {
            handlers?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching a Surface message from {FromHandle}.", message.FromHandle);
        }
    }

    private string? Qualify(string? handle)
        => string.IsNullOrWhiteSpace(handle) ? null : HandleUtilities.EnsurePrefix(handle, handlePrefix);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await stopping.CancelAsync();
        if (quietCheck is not null)
            await quietCheck.DisposeAsync();
        if (socket is { } connection)
            await connection.DisposeAsync();
        if (reading is not null)
            await reading.ContinueWith(_ => { }, TaskScheduler.Default);
        lock (eventLock)
            agentMessageReceived = null;
        http.Dispose();
        stopping.Dispose();
    }
}
