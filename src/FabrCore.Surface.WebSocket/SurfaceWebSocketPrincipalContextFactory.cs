using System.Collections.Concurrent;
using FabrCore.Client.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Surface.WebSocket;

/// <summary>
/// One <see cref="SurfaceWebSocketPrincipalContext"/> per principal, shared by every page and tab that principal has open, so they share
/// one connection and see the same answers.
/// </summary>
public sealed class SurfaceWebSocketPrincipalContextFactory : ISurfacePrincipalContextFactory, IAsyncDisposable
{
    private readonly IHttpClientFactory clients;
    private readonly IOptions<SurfaceWebSocketOptions> options;
    private readonly ILoggerFactory loggers;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, Lazy<SurfaceWebSocketPrincipalContext>> contexts = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool disposed;

    public SurfaceWebSocketPrincipalContextFactory(
        IHttpClientFactory clients,
        IOptions<SurfaceWebSocketOptions> options,
        ILoggerFactory loggers,
        TimeProvider? time = null)
    {
        this.clients = clients;
        this.options = options;
        this.loggers = loggers;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Opens a principal's connection to the host. Tests replace it.</summary>
    internal Func<HttpClient, FabrCoreWebSocketClientOptions, IFabrCoreWebSocketClient>? OpenSocket { get; set; }

    public async Task<ISurfacePrincipalContext> CreateAsync(string handle, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        var context = NewContext(handle);
        try
        {
            await context.ConnectAsync(cancellationToken);
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The principal's shared context, connected. A connection that can't be opened throws <see cref="SurfaceConnectionException"/>,
    /// and the next call tries again with the same context.
    /// </summary>
    public async Task<ISurfacePrincipalContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        while (true)
        {
            var entry = contexts.GetOrAdd(handle, h => new Lazy<SurfaceWebSocketPrincipalContext>(() => NewContext(h)));
            var context = entry.Value;
            if (context.IsDisposed)
            {
                contexts.TryRemove(new KeyValuePair<string, Lazy<SurfaceWebSocketPrincipalContext>>(handle, entry));
                continue;
            }

            await context.ConnectAsync(cancellationToken);
            return context;
        }
    }

    public async Task<bool> ReleaseAsync(string handle)
    {
        if (disposed || string.IsNullOrWhiteSpace(handle) || !contexts.TryRemove(handle, out var context))
            return false;
        if (context.IsValueCreated)
            await context.Value.DisposeAsync();
        return true;
    }

    public bool HasContext(string handle)
        => !string.IsNullOrWhiteSpace(handle)
           && contexts.TryGetValue(handle, out var context)
           && context.IsValueCreated
           && !context.Value.IsDisposed;

    private SurfaceWebSocketPrincipalContext NewContext(string handle)
    {
        var settings = options.Value;
        var http = clients.CreateClient(settings.HttpClientName);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            http.DefaultRequestHeaders.TryAddWithoutValidation(settings.ApiKeyHeader, settings.ApiKey);
        http.DefaultRequestHeaders.TryAddWithoutValidation(settings.PrincipalHeader, handle);
        var open = OpenSocket ?? ((client, socketOptions) =>
            new FabrCoreWebSocketClient(client, socketOptions, logger: loggers.CreateLogger<FabrCoreWebSocketClient>()));
        return new SurfaceWebSocketPrincipalContext(handle, http, settings, open, loggers.CreateLogger<SurfaceWebSocketPrincipalContext>(), time);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var context in contexts.Values.Where(context => context.IsValueCreated))
            await context.Value.DisposeAsync();
        contexts.Clear();
    }
}
