using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Orleans;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FabrCore.Host.WebSocket
{
    /// <summary>
    /// Middleware to handle WebSocket connections for FabrCore clients.
    /// </summary>
    public class WebSocketMiddleware
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Host.WebSocketMiddleware");
        private static readonly Meter Meter = new("FabrCore.Host.WebSocketMiddleware");

        // Metrics
        private static readonly Counter<long> ConnectionsAcceptedCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.connections.accepted",
            description: "Number of WebSocket connections accepted");

        private static readonly Counter<long> ConnectionsRejectedCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.connections.rejected",
            description: "Number of WebSocket connections rejected");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.websocket.middleware.errors",
            description: "Number of errors in WebSocket middleware");

        private readonly RequestDelegate next;
        private readonly ILogger<WebSocketMiddleware> logger;

        public WebSocketMiddleware(RequestDelegate next, ILogger<WebSocketMiddleware> logger)
        {
            this.next = next;
            this.logger = logger;
        }

        public async Task InvokeAsync(
            HttpContext context,
            IClusterClient clusterClient,
            ILoggerFactory loggerFactory,
            IOptions<FabrCoreHostOptions> hostOptions,
            IWebSocketAuthenticator authenticator)
        {
            using var activity = ActivitySource.StartActivity("InvokeAsync", ActivityKind.Server);

            var configuredPath = hostOptions.Value.WebSocketPath;

            // Check if this is a WebSocket request to the configured endpoint
            if (context.Request.Path == configuredPath && context.WebSockets.IsWebSocketRequest)
            {
                activity?.SetTag("websocket.path", context.Request.Path);
                activity?.SetTag("client.ip", context.Connection.RemoteIpAddress?.ToString());

                // Run pluggable authentication. Default implementation preserves the
                // pre-auth header/query behavior; production hosts register their own.
                var authResult = await authenticator.AuthenticateAsync(context);
                if (!authResult.Allowed || string.IsNullOrWhiteSpace(authResult.UserId))
                {
                    logger.LogWarning(
                        "WebSocket connection rejected by authenticator from {RemoteIp}: {Reason}",
                        context.Connection.RemoteIpAddress, authResult.Reason ?? "(no reason)");

                    ConnectionsRejectedCounter.Add(1,
                        new KeyValuePair<string, object?>("reason", "authentication_failed"));

                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync(authResult.Reason ?? "WebSocket connection denied.");
                    return;
                }

                var userId = authResult.UserId;
                activity?.SetTag("user.id", userId);

                logger.LogInformation("WebSocket connection request from {RemoteIp} for user {UserId}",
                    context.Connection.RemoteIpAddress, userId);

                try
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                    // user.id intentionally omitted from metric tags — it's unbounded
                    // cardinality. Trace/activity tags still carry it for drill-down.
                    ConnectionsAcceptedCounter.Add(1);

                    logger.LogInformation("WebSocket connection accepted for user {UserId}", userId);

                    // Create a new session with the user ID as the handle
                    var sessionLogger = loggerFactory.CreateLogger<WebSocketSession>();
                    var session = new WebSocketSession(webSocket, clusterClient, sessionLogger, userId, hostOptions.Value);

                    // Capture the upgrade-request's trace context (populated by ASP.NET Core
                    // from an incoming traceparent header, or from our own InvokeAsync activity)
                    // so per-message activities on the session can parent on it when the
                    // message itself carries no trace context.
                    var ambientContext = Activity.Current?.Context ?? default;
                    if (ambientContext != default)
                        session.SetInitialTraceContext(ambientContext);

                    try
                    {
                        // Start processing messages
                        await session.StartAsync(context.RequestAborted);
                    }
                    finally
                    {
                        await session.DisposeAsync();
                    }

                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error handling WebSocket connection for user {UserId}", userId);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    ErrorCounter.Add(1,
                        new KeyValuePair<string, object?>("error.type", "connection_error"));

                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }
            else if (context.Request.Path == configuredPath && !context.WebSockets.IsWebSocketRequest)
            {
                // Request to WebSocket endpoint but not a WebSocket request
                logger.LogWarning("Non-WebSocket request to {WebSocketPath} endpoint", configuredPath);

                ConnectionsRejectedCounter.Add(1,
                    new KeyValuePair<string, object?>("reason", "not_websocket_request"));

                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("This endpoint only accepts WebSocket connections");
            }
            else
            {
                // Not a WebSocket request to our endpoint, pass to next middleware
                await next(context);
            }
        }
    }
}
