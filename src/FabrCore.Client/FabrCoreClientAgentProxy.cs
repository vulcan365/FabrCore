using FabrCore.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace FabrCore.Client
{
    /// <summary>
    /// Base class for component-hosted agent proxies with typed component access.
    /// </summary>
    /// <typeparam name="TComponent">The Blazor component type this agent is attached to.</typeparam>
    public abstract class FabrCoreClientAgentProxy<TComponent> : IFabrCoreClientAgentProxy
        where TComponent : ComponentBase
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Client.FabrCoreClientAgentProxy");
        private static readonly Meter Meter = new("FabrCore.Client.FabrCoreClientAgentProxy");

        private static readonly Counter<long> AgentInitializedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.agent_proxy.initialized",
            description: "Number of client agent proxies initialized");

        private static readonly Counter<long> MessagesProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.client.agent_proxy.messages.processed",
            description: "Number of messages processed by client agent proxy");

        private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "fabrcore.client.agent_proxy.message.duration",
            unit: "ms",
            description: "Duration of client agent proxy message processing");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.client.agent_proxy.errors",
            description: "Number of errors encountered in client agent proxy");

        private readonly IClientContextFactory _clientContextFactory;
        private readonly string _handle;
        private IClientContext? _clientContext;
        private bool _disposed;
        private bool _initialized;

        protected readonly IServiceProvider serviceProvider;
        protected readonly ILoggerFactory loggerFactory;
        protected readonly ILogger logger;
        protected readonly IFabrCoreHostApiClient fabrcoreHostApiClient;

        /// <summary>
        /// Gets the component this agent is attached to.
        /// </summary>
        protected TComponent Component { get; }

        /// <summary>
        /// Gets the client context for cluster communication. Available after InitializeAsync.
        /// </summary>
        protected IClientContext ClientContext => _clientContext
            ?? throw new InvalidOperationException("Agent not initialized. Call InitializeAsync first.");

        /// <summary>
        /// Gets the agent's handle.
        /// </summary>
        public string Handle => _handle;

        /// <summary>
        /// Gets whether the agent has been initialized.
        /// </summary>
        public bool IsInitialized => _initialized;

        protected FabrCoreClientAgentProxy(
            TComponent component,
            IClientContextFactory clientContextFactory,
            string handle,
            IServiceProvider serviceProvider,
            IFabrCoreHostApiClient fabrcoreHostApiClient,
            ILoggerFactory loggerFactory)
        {
            Component = component ?? throw new ArgumentNullException(nameof(component));
            _clientContextFactory = clientContextFactory ?? throw new ArgumentNullException(nameof(clientContextFactory));
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            this.fabrcoreHostApiClient = fabrcoreHostApiClient ?? throw new ArgumentNullException(nameof(fabrcoreHostApiClient));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.logger = loggerFactory.CreateLogger(GetType());
        }

        /// <summary>
        /// Initializes the agent proxy.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized)
            {
                logger.LogDebug("FabrCoreClientAgentProxy already initialized - Handle: {Handle}", Handle);
                return;
            }

            using var activity = ActivitySource.StartActivity("InitializeAsync", ActivityKind.Internal);
            activity?.SetTag("agent.handle", Handle);

            logger.LogInformation("Initializing FabrCoreClientAgentProxy - Handle: {Handle}, ProxyType: {ProxyType}",
                Handle, GetType().Name);

            try
            {
                // Get or create the client context for this handle
                logger.LogDebug("Getting or creating client context - Handle: {Handle}", Handle);
                _clientContext = await _clientContextFactory.GetOrCreateAsync(_handle, cancellationToken);
                logger.LogDebug("Client context obtained - Handle: {Handle}, ContextHandle: {ContextHandle}",
                    Handle, _clientContext.Handle);

                // Subscribe to incoming messages
                logger.LogDebug("Subscribing to AgentMessageReceived event - Handle: {Handle}", Handle);
                _clientContext.AgentMessageReceived += OnAgentMessageReceived;
                logger.LogDebug("Subscribed to AgentMessageReceived event - Handle: {Handle}", Handle);

                logger.LogDebug("Calling OnInitializeAsync - Handle: {Handle}", Handle);
                await OnInitializeAsync();
                logger.LogDebug("OnInitializeAsync completed - Handle: {Handle}", Handle);

                _initialized = true;

                AgentInitializedCounter.Add(1,
                    new KeyValuePair<string, object?>("agent.handle", Handle));

                logger.LogInformation("FabrCoreClientAgentProxy initialized successfully - Handle: {Handle}, ProxyType: {ProxyType}",
                    Handle, GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize FabrCoreClientAgentProxy - Handle: {Handle}, ProxyType: {ProxyType}, ExceptionType: {ExceptionType}",
                    Handle, GetType().Name, ex.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "initialization_failed"),
                    new KeyValuePair<string, object?>("agent.handle", Handle));
                throw;
            }
        }

        /// <summary>
        /// Override to perform initialization logic.
        /// </summary>
        public virtual Task OnInitializeAsync() => Task.CompletedTask;

        /// <summary>
        /// Override to handle incoming messages.
        /// </summary>
        public abstract Task<AgentMessage> OnMessageAsync(AgentMessage message);

        /// <summary>
        /// Override to handle incoming events.
        /// </summary>
        public virtual Task OnEventAsync(AgentMessage message) => Task.CompletedTask;

        /// <summary>
        /// Sends a message and waits for a response.
        /// </summary>
        protected Task<AgentMessage> SendAndReceiveMessageAsync(AgentMessage message)
        {
            return ClientContext.SendAndReceiveMessage(message);
        }

        /// <summary>
        /// Sends a fire-and-forget message.
        /// </summary>
        protected Task SendMessageAsync(AgentMessage message)
        {
            return ClientContext.SendMessage(message);
        }

        /// <summary>
        /// Sends a fire-and-forget event to an agent's AgentEvent stream.
        /// Events are delivered to the agent's OnEvent handler, not OnMessage.
        /// If streamName is provided, publishes to the named event stream.
        /// </summary>
        protected Task SendEventAsync(AgentMessage message, string? streamName = null)
        {
            return ClientContext.SendEvent(message, streamName);
        }

        /// <summary>
        /// Requests a UI update on the component. Must be called from the component's sync context
        /// or wrapped in InvokeAsync for thread safety in Blazor Server.
        /// </summary>
        protected void RequestComponentUpdate()
        {
            // Uses reflection to call StateHasChanged on the component
            // since it's a protected method on ComponentBase
            var method = typeof(ComponentBase).GetMethod(
                "StateHasChanged",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(Component, null);
        }

        /// <summary>
        /// Executes an action on the component's synchronization context and triggers a UI update.
        /// Use this when modifying component state from background threads (e.g., AI tool callbacks).
        /// </summary>
        protected async Task InvokeAsync(Func<Task> action)
        {
            var method = typeof(ComponentBase).GetMethod(
                "InvokeAsync",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(Func<Task>) },
                null);

            if (method != null)
            {
                var task = (Task?)method.Invoke(Component, new object[] { action });
                if (task != null) await task;
            }
        }

        /// <summary>
        /// Executes a synchronous action on the component's synchronization context.
        /// </summary>
        protected async Task InvokeAsync(Action action)
        {
            var method = typeof(ComponentBase).GetMethod(
                "InvokeAsync",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(Action) },
                null);

            if (method != null)
            {
                var task = (Task?)method.Invoke(Component, new object[] { action });
                if (task != null) await task;
            }
        }

        private async void OnAgentMessageReceived(object? sender, AgentMessage message)
        {
            logger.LogDebug("OnAgentMessageReceived invoked - Handle: {Handle}, Disposed: {Disposed}, MessageFrom: {FromHandle}, MessageTo: {ToHandle}, MessageType: {MessageType}",
                Handle, _disposed, message.FromHandle, message.ToHandle, message.MessageType);

            if (_disposed)
            {
                logger.LogWarning("OnAgentMessageReceived skipped - agent is disposed. Handle: {Handle}, From: {FromHandle}",
                    Handle, message.FromHandle);
                return;
            }

            // Only process messages intended for this agent
            if (message.ToHandle != Handle)
            {
                logger.LogDebug("OnAgentMessageReceived skipped - ToHandle mismatch. Expected: {ExpectedHandle}, Actual: {ActualToHandle}, From: {FromHandle}",
                    Handle, message.ToHandle, message.FromHandle);
                return;
            }

            logger.LogInformation("Processing message - Handle: {Handle}, From: {FromHandle}, MessageType: {MessageType}",
                Handle, message.FromHandle, message.MessageType ?? "message");

            using var activity = ActivitySource.StartActivity("OnAgentMessageReceived", ActivityKind.Server);
            activity?.SetTag("agent.handle", Handle);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.type", message.MessageType);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                if (message.MessageType == "event")
                {
                    logger.LogDebug("Calling OnEventAsync - Handle: {Handle}, From: {FromHandle}", Handle, message.FromHandle);
                    await OnEventAsync(message);
                    logger.LogDebug("OnEventAsync completed - Handle: {Handle}, From: {FromHandle}", Handle, message.FromHandle);
                }
                else
                {
                    logger.LogDebug("Calling OnMessageAsync - Handle: {Handle}, From: {FromHandle}", Handle, message.FromHandle);
                    var response = await OnMessageAsync(message);
                    logger.LogDebug("OnMessageAsync completed - Handle: {Handle}, From: {FromHandle}, ResponseLength: {ResponseLength}",
                        Handle, message.FromHandle, response?.Message?.Length ?? 0);

                    // Send response back to the sender
                    if (response != null && !string.IsNullOrEmpty(message.FromHandle))
                    {
                        logger.LogDebug("Sending response back to sender - Handle: {Handle}, To: {ToHandle}", Handle, message.FromHandle);
                        await SendMessageAsync(response);
                        logger.LogDebug("Response sent successfully - Handle: {Handle}, To: {ToHandle}", Handle, message.FromHandle);
                    }
                }

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                MessageProcessingDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("agent.handle", Handle),
                    new KeyValuePair<string, object?>("message.from", message.FromHandle));

                MessagesProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("agent.handle", Handle),
                    new KeyValuePair<string, object?>("message.type", message.MessageType ?? "message"));

                logger.LogInformation("Message processed successfully - Handle: {Handle}, From: {FromHandle}, Duration: {Duration}ms",
                    Handle, message.FromHandle, elapsed);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                logger.LogError(ex, "Error processing message - Handle: {Handle}, From: {FromHandle}, MessageType: {MessageType}, Duration: {Duration}ms, ExceptionType: {ExceptionType}",
                    Handle, message.FromHandle, message.MessageType ?? "message", elapsed, ex.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "message_processing_failed"),
                    new KeyValuePair<string, object?>("agent.handle", Handle));
            }
        }

        public virtual async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            using var activity = ActivitySource.StartActivity("DisposeAsync", ActivityKind.Internal);
            activity?.SetTag("agent.handle", Handle);

            if (_clientContext != null)
            {
                _clientContext.AgentMessageReceived -= OnAgentMessageReceived;
            }

            logger.LogInformation("FabrCoreClientAgentProxy disposed - Handle: {Handle}", Handle);
            activity?.SetStatus(ActivityStatusCode.Ok);

            GC.SuppressFinalize(this);
        }
    }
}
