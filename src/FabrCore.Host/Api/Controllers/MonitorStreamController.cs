using FabrCore.Core;
using FabrCore.Core.Acl;
using System.Text.Json;
using System.Threading.Channels;
using FabrCore.Core.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    /// <summary>
    /// Server-Sent Events stream of monitor activity. Subscribes to the
    /// <see cref="IAgentMessageMonitor"/>'s in-process events and relays
    /// each one as a named SSE event. Chosen over SignalR to keep dashboards
    /// dependency-free and proxy-friendly.
    /// </summary>
    [ApiController]
    [Route("fabrcoreapi/monitor/stream")]
    public class MonitorStreamController : Controller
    {
        // Bounded per-client queue with drop-oldest: a slow reader can't memory-pressure
        // the monitor. Heartbeats keep proxies from idle-timing the connection.
        private const int ClientQueueCapacity = 512;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IAgentMessageMonitor _monitor;
        private readonly IAclProvider _aclProvider;
        private readonly ILogger<MonitorStreamController> _logger;

        public MonitorStreamController(
            IAgentMessageMonitor monitor,
            IAclProvider aclProvider,
            ILogger<MonitorStreamController> logger)
        {
            _monitor = monitor;
            _aclProvider = aclProvider;
            _logger = logger;
        }

        [HttpGet("")]
        public async Task Get(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromQuery] string? agentHandle = null,
            [FromQuery] string? channels = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteRawAsync("x-user-handle header is required for monitor stream access", cancellationToken);
                return;
            }

            if (!string.IsNullOrWhiteSpace(agentHandle) && !await CanReadAgentAsync(userHandle, agentHandle))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await WriteRawAsync($"Access denied: '{userHandle}' cannot Read monitor data for '{agentHandle}'.", cancellationToken);
                return;
            }

            // channels is a comma-separated filter: "messages,events,llm-calls".
            // Default is all three.
            var include = ParseChannels(channels);

            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache, no-store";
            Response.Headers["Connection"] = "keep-alive";
            // Defeats nginx default buffering — otherwise events stall.
            Response.Headers["X-Accel-Buffering"] = "no";

            var queue = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(ClientQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            void OnMessage(MonitoredMessage m)
            {
                if (!include.Messages) return;
                if (!string.IsNullOrEmpty(agentHandle) && m.AgentHandle != agentHandle) return;
                _ = QueueWhenReadableAsync(
                    userHandle,
                    queue,
                    "message",
                    m,
                    MessageHandles(m),
                    cancellationToken);
            }

            void OnEvent(MonitoredEvent e)
            {
                if (!include.Events) return;
                if (!string.IsNullOrEmpty(agentHandle) && e.AgentHandle != agentHandle) return;
                _ = QueueWhenReadableAsync(
                    userHandle,
                    queue,
                    "event",
                    e,
                    [e.AgentHandle],
                    cancellationToken);
            }

            void OnLlmCall(MonitoredLlmCall c)
            {
                if (!include.LlmCalls) return;
                if (!string.IsNullOrEmpty(agentHandle) && c.AgentHandle != agentHandle) return;
                _ = QueueWhenReadableAsync(
                    userHandle,
                    queue,
                    "llm-call",
                    c,
                    [c.AgentHandle],
                    cancellationToken);
            }

            _monitor.OnMessageRecorded += OnMessage;
            _monitor.OnEventRecorded += OnEvent;
            _monitor.OnLlmCallRecorded += OnLlmCall;

            _logger.LogInformation(
                "Monitor SSE stream opened (agent={AgentHandle}, channels={Channels})",
                agentHandle ?? "*",
                channels ?? "all");

            try
            {
                // Opening comment + retry hint so well-behaved EventSource clients reconnect on drop.
                await WriteRawAsync(": connected\nretry: 5000\n\n", cancellationToken);

                using var heartbeatTimer = new PeriodicTimer(HeartbeatInterval);
                var heartbeatTask = SendHeartbeatsAsync(heartbeatTimer, cancellationToken);

                await foreach (var evt in queue.Reader.ReadAllAsync(cancellationToken))
                {
                    var json = JsonSerializer.Serialize(evt.Payload, evt.Payload.GetType(), SerializerOptions);
                    await WriteRawAsync($"event: {evt.EventName}\ndata: {json}\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — expected.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitor SSE stream terminated unexpectedly");
            }
            finally
            {
                _monitor.OnMessageRecorded -= OnMessage;
                _monitor.OnEventRecorded -= OnEvent;
                _monitor.OnLlmCallRecorded -= OnLlmCall;
                queue.Writer.TryComplete();
                _logger.LogInformation("Monitor SSE stream closed (agent={AgentHandle})", agentHandle ?? "*");
            }
        }

        private async Task SendHeartbeatsAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    // SSE comment line — invisible to EventSource but keeps the connection warm.
                    await WriteRawAsync($": heartbeat {DateTimeOffset.UtcNow:O}\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Monitor SSE heartbeat loop exited");
            }
        }

        private Task WriteRawAsync(string payload, CancellationToken cancellationToken)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            return Response.Body.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        }

        private static ChannelFilter ParseChannels(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new ChannelFilter(true, true, true);

            var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool m = false, e = false, l = false;
            foreach (var p in parts)
            {
                switch (p.ToLowerInvariant())
                {
                    case "messages": case "message": m = true; break;
                    case "events": case "event": e = true; break;
                    case "llm-calls": case "llm": case "llm-call": l = true; break;
                }
            }
            return new ChannelFilter(m, e, l);
        }

        private async Task QueueWhenReadableAsync(
            string userHandle,
            Channel<SseEvent> queue,
            string eventName,
            object payload,
            IEnumerable<string?> handles,
            CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                foreach (var handle in handles)
                {
                    if (await CanReadAgentAsync(userHandle, handle))
                    {
                        queue.Writer.TryWrite(new SseEvent(eventName, payload));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Monitor SSE ACL filter failed for event {EventName}", eventName);
            }
        }

        private async Task<bool> CanReadAgentAsync(string userHandle, string? agentHandle)
        {
            if (string.IsNullOrWhiteSpace(agentHandle))
            {
                return false;
            }

            var (targetUserHandle, targetAgentHandle) = HandleUtilities.ParseHandle(agentHandle);
            if (string.IsNullOrWhiteSpace(targetUserHandle) || string.IsNullOrWhiteSpace(targetAgentHandle))
            {
                return false;
            }

            if (string.Equals(userHandle, targetUserHandle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var result = await _aclProvider.EvaluateAsync(userHandle, targetUserHandle, targetAgentHandle, AclPermission.Read);
            return result.Allowed;
        }

        private static IEnumerable<string?> MessageHandles(MonitoredMessage message)
        {
            yield return message.AgentHandle;
            yield return message.ToHandle;
            yield return message.FromHandle;
            yield return message.DeliverToHandle;
            yield return message.OnBehalfOfHandle;
        }

        private readonly record struct ChannelFilter(bool Messages, bool Events, bool LlmCalls);
        private readonly record struct SseEvent(string EventName, object Payload);
    }
}
