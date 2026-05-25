using System.Collections.Concurrent;
using FabrCore.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Surface;

public sealed class SurfaceClientContextFactory : ISurfaceClientContextFactory, IAsyncDisposable
{
    private readonly IClusterClient clusterClient;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<SurfaceClientContextFactory> logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<SurfaceClientContext>>> contextCache = new();
    private volatile bool disposed;

    public SurfaceClientContextFactory(IClusterClient clusterClient, ILoggerFactory loggerFactory)
    {
        this.clusterClient = clusterClient;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<SurfaceClientContextFactory>();
    }

    public async Task<ISurfaceClientContext> CreateAsync(string handle, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        return await CreateContextInternalAsync(handle, cancellationToken);
    }

    public async Task<ISurfaceClientContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        try
        {
            var lazyContext = contextCache.GetOrAdd(handle, h =>
                new Lazy<Task<SurfaceClientContext>>(() => CreateContextInternalAsync(h, cancellationToken)));

            var context = await lazyContext.Value;
            if (!context.IsDisposed)
            {
                return context;
            }

            contextCache.TryRemove(handle, out _);
            return await GetOrCreateAsync(handle, cancellationToken);
        }
        catch
        {
            contextCache.TryRemove(handle, out _);
            throw;
        }
    }

    public async Task<bool> ReleaseAsync(string handle)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        if (!contextCache.TryRemove(handle, out var lazyContext))
        {
            return false;
        }

        if (lazyContext.IsValueCreated)
        {
            try
            {
                var context = await lazyContext.Value;
                await context.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing cached Surface context for handle {Handle}.", handle);
            }
        }

        return true;
    }

    public bool HasContext(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        if (!contextCache.TryGetValue(handle, out var lazyContext) || !lazyContext.IsValueCreated)
        {
            return false;
        }

        try
        {
            var task = lazyContext.Value;
            return task.IsCompletedSuccessfully && !task.Result.IsDisposed;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        var handles = contextCache.Keys.ToList();
        foreach (var handle in handles)
        {
            await ReleaseAsync(handle);
        }

        GC.SuppressFinalize(this);
    }

    private async Task<SurfaceClientContext> CreateContextInternalAsync(string handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clientGrain = clusterClient.GetGrain<IClientGrain>(handle);
        var context = SurfaceClientContext.CreateUninitialized(
            clusterClient,
            loggerFactory.CreateLogger<SurfaceClientContext>(),
            handle,
            clientGrain);

        var observerRef = clusterClient.CreateObjectReference<IClientGrainObserver>(context);
        context.SetObserverReference(observerRef);

        cancellationToken.ThrowIfCancellationRequested();
        await clientGrain.Subscribe(observerRef);

        return context;
    }

    private static void ValidateHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new ArgumentNullException(nameof(handle), "Handle cannot be null or empty.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);
}
