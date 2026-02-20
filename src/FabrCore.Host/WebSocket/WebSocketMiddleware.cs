using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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

        public async Task InvokeAsync(HttpContext context, IClusterClient clusterClient, ILoggerFactory loggerFactory)
        {
            using var activity = ActivitySource.StartActivity("InvokeAsync", ActivityKind.Server);

            // Check if this is a WebSocket request to our endpoint
            if (context.Request.Path == "/ws" && context.WebSockets.IsWebSocketRequest)
            {
                activity?.SetTag("websocket.path", context.Request.Path);
                activity?.SetTag("client.ip", context.Connection.RemoteIpAddress?.ToString());

                // Extract client ID from header or query parameter
                // Try header first (preferred), then fall back to query parameter (for browser compatibility)
                string? userId = null;

                if (context.Request.Headers.TryGetValue("x-fabrcore-userid", out var userIdValues))
                {
                    userId = userIdValues.FirstOrDefault();
                    logger.LogDebug("User ID from header: {UserId}", userId);
                }
                else if (context.Request.Query.TryGetValue("userid", out var queryValues))
                {
                    userId = queryValues.FirstOrDefault();
                    logger.LogDebug("User ID from query parameter: {UserId}", userId);
                }

                if (string.IsNullOrWhiteSpace(userId))
                {
                    logger.LogWarning("WebSocket connection rejected - missing or empty user ID from {RemoteIp}",
                        context.Connection.RemoteIpAddress);

                    ConnectionsRejectedCounter.Add(1,
                        new KeyValuePair<string, object?>("reason", "missing_or_empty_userid"));

                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Missing required user ID. Provide via x-fabrcore-userid header or userid query parameter.");
                    return;
                }

                activity?.SetTag("user.id", userId);

                logger.LogInformation("WebSocket connection request from {RemoteIp} for user {UserId}",
                    context.Connection.RemoteIpAddress, userId);

                try
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                    ConnectionsAcceptedCounter.Add(1,
                        new KeyValuePair<string, object?>("user.id", userId));

                    logger.LogInformation("WebSocket connection accepted for user {UserId}", userId);

                    // Create a new session with the user ID as the handle
                    var sessionLogger = loggerFactory.CreateLogger<WebSocketSession>();
                    var session = new WebSocketSession(webSocket, clusterClient, sessionLogger, userId);

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
                        new KeyValuePair<string, object?>("error.type", "connection_error"),
                        new KeyValuePair<string, object?>("user.id", userId));

                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }
            else if (context.Request.Path == "/ws" && !context.WebSockets.IsWebSocketRequest)
            {
                // Request to WebSocket endpoint but not a WebSocket request
                logger.LogWarning("Non-WebSocket request to /ws endpoint");

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
