using FabrCore.Core;
using FabrCore.Core.Interfaces;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Orleans;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FabrCore.Host.WebSocket
{
    /// <summary>
    /// WebSocket session that wraps ClientGrain similar to how ClientContext works.
    /// Handles commands via AgentMessage with MessageType="command".
    /// </summary>
    public class WebSocketSession : IClientGrainObserver, IAsyncDisposable
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.WebSocketSession");
        private static readonly Meter Meter = new("FabrCore.Host.WebSocketSession");

        // Metrics
        private static readonly Counter<long> SessionsCreatedCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.sessions.created",
            description: "Number of WebSocket sessions created");

        private static readonly Counter<long> SessionsClosedCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.sessions.closed",
            description: "Number of WebSocket sessions closed");

        private static readonly Counter<long> MessagesReceivedCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.messages.received",
            description: "Number of messages received from WebSocket");

        private static readonly Counter<long> MessagesSentCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.messages.sent",
            description: "Number of messages sent to WebSocket");

        private static readonly Counter<long> CommandsProcessedCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.commands.processed",
            description: "Number of commands processed");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.errors",
            description: "Number of errors encountered");

        private readonly System.Net.WebSockets.WebSocket webSocket;
        private readonly IClusterClient clusterClient;
        private readonly ILogger<WebSocketSession> logger;
        private IClientGrain? clientGrain;
        private IClientGrainObserver? observerRef;
        private readonly string handle;
        private bool disposed;
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly SemaphoreSlim sendLock = new(1, 1);

        public WebSocketSession(
            System.Net.WebSockets.WebSocket webSocket,
            IClusterClient clusterClient,
            ILogger<WebSocketSession> logger,
            string userId)
        {
            this.webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
            this.clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.handle = userId ?? throw new ArgumentNullException(nameof(userId));
            this.cancellationTokenSource = new CancellationTokenSource();

            SessionsCreatedCounter.Add(1, new KeyValuePair<string, object?>("user.id", handle));
            logger.LogInformation("WebSocketSession created for user: {UserId}", handle);
        }

        /// <summary>
        /// Start the WebSocket session message processing loop.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("StartAsync", ActivityKind.Server);
            activity?.SetTag("user.id", handle);

            logger.LogInformation("Starting WebSocket session for user: {UserId}", handle);

            try
            {
                // Initialize the client grain connection
                await InitializeClientGrainAsync();

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    cancellationTokenSource.Token);

                await ProcessMessagesAsync(linkedCts.Token);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is WebSocketException)
            {
                logger.LogInformation("WebSocket session ended for user {UserId}: {Message}", handle, ex.Message);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in WebSocket session for user {UserId}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "session_error"),
                    new KeyValuePair<string, object?>("user.id", handle));
                throw;
            }
        }

        /// <summary>
        /// Initialize the ClientGrain connection and subscribe to the observer.
        /// </summary>
        private async Task InitializeClientGrainAsync()
        {
            using var activity = ActivitySource.StartActivity("InitializeClientGrain", ActivityKind.Internal);
            activity?.SetTag("user.id", handle);

            logger.LogInformation("Initializing ClientGrain for user: {UserId}", handle);

            try
            {
                // Get the grain with the handle as primary key
                clientGrain = clusterClient.GetGrain<IClientGrain>(handle);
                logger.LogDebug("Client grain obtained for user: {UserId}", handle);

                // Create observer reference and subscribe to the grain
                observerRef = clusterClient.CreateObjectReference<IClientGrainObserver>(this);
                logger.LogDebug("Observer reference created for user: {UserId}", handle);

                await clientGrain.Subscribe(observerRef);

                logger.LogInformation("WebSocket session subscribed to ClientGrain for user: {UserId}", handle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize ClientGrain for user: {UserId}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "client_grain_init_failed"),
                    new KeyValuePair<string, object?>("user.id", handle));
                throw;
            }
        }

        private async Task ProcessMessagesAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 4];

            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var activity = ActivitySource.StartActivity("ProcessMessage", ActivityKind.Server);

                try
                {
                    var result = await ReceiveFullMessageAsync(buffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        logger.LogInformation("WebSocket close message received");
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            cancellationToken);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text && result.Message != null)
                    {
                        MessagesReceivedCounter.Add(1);
                        activity?.SetTag("message.length", result.Message.Length);

                        await HandleMessageAsync(result.Message, cancellationToken);
                    }

                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing WebSocket message");
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "message_processing_error"));

                    // Send error back to client
                    await SendErrorAsync(ex.Message, cancellationToken);
                }
            }
        }

        private async Task<(WebSocketMessageType MessageType, string? Message)> ReceiveFullMessageAsync(
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                ms.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(ms, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(cancellationToken);
                return (result.MessageType, message);
            }

            return (result.MessageType, null);
        }

        private async Task HandleMessageAsync(string messageJson, CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("HandleMessage", ActivityKind.Internal);

            try
            {
                var agentMessage = JsonSerializer.Deserialize<AgentMessage>(messageJson);

                if (agentMessage == null)
                {
                    throw new InvalidOperationException("Failed to deserialize message");
                }

                activity?.SetTag("message.type", agentMessage.MessageType);
                activity?.SetTag("message.from", agentMessage.FromHandle);
                activity?.SetTag("message.to", agentMessage.ToHandle);

                // Check if this is a command message
                if (agentMessage.MessageType?.ToLower() == "command")
                {
                    await ProcessCommandAsync(agentMessage, cancellationToken);
                }
                else if (agentMessage.MessageType?.ToLower() == "event")
                {
                    await ProcessEventMessageAsync(agentMessage, cancellationToken);
                }
                else
                {
                    // Regular message - route through ClientGrain
                    await ProcessRegularMessageAsync(agentMessage, cancellationToken);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling message");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private async Task ProcessCommandAsync(AgentMessage command, CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("ProcessCommand", ActivityKind.Internal);

            var commandName = command.Message?.ToLower();
            activity?.SetTag("command.name", commandName);
            activity?.SetTag("user.id", handle);

            logger.LogInformation("Processing command: {CommandName} for user: {UserId}", commandName, handle);

            CommandsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("command.name", commandName),
                new KeyValuePair<string, object?>("user.id", handle));

            try
            {
                switch (commandName)
                {
                    case "createagent":
                        await HandleCreateAgentCommandAsync(command, cancellationToken);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown command: {commandName}");
                }

                // Send success response
                var response = new AgentMessage
                {
                    MessageType = "command_response",
                    Message = "success",
                    Kind = MessageKind.Response,
                    TraceId = command.TraceId,
                    State = new Dictionary<string, string>
                    {
                        ["command"] = commandName ?? "unknown",
                        ["status"] = "success"
                    }
                };

                await SendMessageAsync(response, cancellationToken);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing command: {CommandName} for user: {UserId}", commandName, handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "command_error"),
                    new KeyValuePair<string, object?>("command.name", commandName),
                    new KeyValuePair<string, object?>("user.id", handle));

                // Send error response
                var errorResponse = new AgentMessage
                {
                    MessageType = "command_response",
                    Message = "error",
                    Kind = MessageKind.Response,
                    TraceId = command.TraceId,
                    State = new Dictionary<string, string>
                    {
                        ["command"] = commandName ?? "unknown",
                        ["status"] = "error",
                        ["error"] = ex.Message
                    }
                };

                await SendMessageAsync(errorResponse, cancellationToken);
            }
        }

        private async Task HandleCreateAgentCommandAsync(AgentMessage command, CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("HandleCreateAgent", ActivityKind.Internal);

            if (clientGrain == null)
            {
                throw new InvalidOperationException("Client not initialized.");
            }

            // Parse agent configuration from command State or DataType/Data
            AgentConfiguration? agentConfig = null;

            if (command.Data != null && !string.IsNullOrEmpty(command.DataType))
            {
                if (command.DataType.ToLower() == "json" || command.DataType.ToLower() == "application/json")
                {
                    var configJson = Encoding.UTF8.GetString(command.Data);
                    agentConfig = JsonSerializer.Deserialize<AgentConfiguration>(configJson);
                }
            }
            else if (command.State != null)
            {
                // Build configuration from State dictionary
                agentConfig = new AgentConfiguration
                {
                    Handle = command.State.GetValueOrDefault("agentHandle"),
                    AgentType = command.State.GetValueOrDefault("agentType"),
                    Models = command.State.GetValueOrDefault("models"),
                    SystemPrompt = command.State.GetValueOrDefault("systemPrompt"),
                    Description = command.State.GetValueOrDefault("description")
                };

                // Parse Args if provided as JSON string
                if (command.State.TryGetValue("args", out var argsJson) && !string.IsNullOrEmpty(argsJson))
                {
                    var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argsJson);
                    if (args != null)
                    {
                        agentConfig.Args = args;
                    }
                }
            }

            if (agentConfig == null)
            {
                throw new ArgumentException("Agent configuration is required in command Data or State");
            }

            activity?.SetTag("agent.type", agentConfig.AgentType);
            activity?.SetTag("agent.handle", agentConfig.Handle);
            activity?.SetTag("client.handle", handle);

            logger.LogInformation("Creating agent - Type: {AgentType}, Handle: {AgentHandle}",
                agentConfig.AgentType, agentConfig.Handle);

            await clientGrain.CreateAgent(agentConfig);

            logger.LogInformation("Agent created successfully: {AgentHandle}", agentConfig.Handle);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        private async Task ProcessRegularMessageAsync(AgentMessage message, CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("ProcessRegularMessage", ActivityKind.Internal);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);

            if (clientGrain == null)
            {
                throw new InvalidOperationException("Client not initialized.");
            }

            logger.LogDebug("Processing regular message - From: {FromHandle}, To: {ToHandle}",
                message.FromHandle, message.ToHandle);

            try
            {
                // Determine if we should wait for response based on message properties
                // If the message expects a response (Kind == Request), use SendAndReceiveMessage
                if (message.Kind == MessageKind.Request)
                {
                    var response = await clientGrain.SendAndReceiveMessage(message);
                    await SendMessageAsync(response, cancellationToken);
                }
                else
                {
                    // Fire and forget (OneWay or Response)
                    await clientGrain.SendMessage(message);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing regular message");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private async Task ProcessEventMessageAsync(AgentMessage message, CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("ProcessEventMessage", ActivityKind.Internal);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);

            if (clientGrain == null)
            {
                throw new InvalidOperationException("Client not initialized.");
            }

            logger.LogDebug("Processing event message - From: {FromHandle}, To: {ToHandle}",
                message.FromHandle, message.ToHandle);

            try
            {
                // Extract optional stream name from Args
                string? streamName = null;
                if (message.Args != null && message.Args.TryGetValue("streamName", out var sn) && !string.IsNullOrEmpty(sn))
                {
                    streamName = sn;
                    activity?.SetTag("stream.name", streamName);
                }

                await clientGrain.SendEvent(message, streamName);

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing event message");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        // IClientGrainObserver implementation
        public void OnMessageReceived(AgentMessage message)
        {
            using var activity = ActivitySource.StartActivity("OnMessageReceived", ActivityKind.Consumer);
            activity?.SetTag("client.handle", handle);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);

            logger.LogInformation("Observer received message - From: {FromHandle}, To: {ToHandle}",
                message.FromHandle, message.ToHandle);

            try
            {
                // Send the message to the WebSocket client
                // Use Task.Run to avoid blocking the Orleans callback
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendMessageAsync(message, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error sending message to WebSocket");
                        ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "websocket_send_error"));
                    }
                });

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in observer callback");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "observer_error"));
                throw;
            }
        }

        private async Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken)
        {
            using var activity = ActivitySource.StartActivity("SendMessage", ActivityKind.Producer);

            try
            {
                var json = JsonSerializer.Serialize(message);
                var bytes = Encoding.UTF8.GetBytes(json);

                await sendLock.WaitAsync(cancellationToken);
                try
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            cancellationToken);

                        MessagesSentCounter.Add(1);
                        logger.LogDebug("Message sent to WebSocket - Size: {Size} bytes", bytes.Length);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                    }
                    else
                    {
                        logger.LogWarning("WebSocket not open, cannot send message. State: {State}", webSocket.State);
                    }
                }
                finally
                {
                    sendLock.Release();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending message to WebSocket");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "send_error"));
                throw;
            }
        }

        private async Task SendErrorAsync(string errorMessage, CancellationToken cancellationToken)
        {
            try
            {
                var errorMsg = new AgentMessage
                {
                    MessageType = "error",
                    Message = errorMessage,
                    Kind = MessageKind.Response
                };

                await SendMessageAsync(errorMsg, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending error message to WebSocket");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            using var activity = ActivitySource.StartActivity("DisposeAsync", ActivityKind.Internal);
            activity?.SetTag("client.handle", handle);

            logger.LogInformation("Disposing WebSocket session: {Handle}", handle);

            try
            {
                // Cancel any ongoing operations
                cancellationTokenSource.Cancel();

                // Unsubscribe if we have an active subscription
                if (clientGrain != null && observerRef != null)
                {
                    try
                    {
                        await clientGrain.Unsubscribe(observerRef);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error unsubscribing during dispose");
                    }
                }

                // Close the WebSocket
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Session disposed",
                        CancellationToken.None);
                }

                webSocket.Dispose();
                cancellationTokenSource.Dispose();
                sendLock.Dispose();

                disposed = true;

                SessionsClosedCounter.Add(1);

                logger.LogInformation("WebSocket session disposed: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error disposing WebSocket session: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1, new KeyValuePair<string, object?>("error.type", "dispose_error"));

                disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }
}
