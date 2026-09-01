using FabrCore.Host.A2A.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Writes A2A streaming responses as Server-Sent Events.
/// </summary>
/// <remarks>
/// A slow agent can leave the connection quiet for longer than an intermediary is willing to wait,
/// so the writer emits SSE comment lines on a timer. Comments are part of the SSE framing and are
/// ignored by every conforming client, which keeps the keep-alive out of the A2A event sequence.
/// </remarks>
internal sealed class A2ASseWriter : IAsyncDisposable
{
    private readonly HttpContext _context;
    private readonly ILogger _logger;
    private readonly Timer? _heartbeat;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public A2ASseWriter(HttpContext context, TimeSpan heartbeatInterval, ILogger logger)
    {
        _context = context;
        _logger = logger;

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";

        // Reverse proxies that buffer by default (nginx among them) hold an SSE body until the
        // response ends, which defeats streaming entirely.
        response.Headers["X-Accel-Buffering"] = "no";

        if (heartbeatInterval > TimeSpan.Zero)
        {
            _heartbeat = new Timer(
                static state => _ = ((A2ASseWriter)state!).WriteHeartbeatAsync(),
                this,
                heartbeatInterval,
                heartbeatInterval);
        }
    }

    /// <summary>Writes one SSE <c>data:</c> frame carrying the serialized payload.</summary>
    public async Task WriteAsync(object payload)
    {
        var json = A2AJson.Serialize(payload);
        await _writeLock.WaitAsync(_context.RequestAborted);
        try
        {
            await _context.Response.WriteAsync($"data: {json}\n\n", _context.RequestAborted);
            await _context.Response.Body.FlushAsync(_context.RequestAborted);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteHeartbeatAsync()
    {
        if (!await _writeLock.WaitAsync(0))
        {
            // A real frame is going out right now; that serves the same purpose.
            return;
        }

        try
        {
            await _context.Response.WriteAsync(": keep-alive\n\n", _context.RequestAborted);
            await _context.Response.Body.FlushAsync(_context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A2A stream keep-alive could not be written; the client has most likely gone away.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync();
        }

        _writeLock.Dispose();
    }
}
