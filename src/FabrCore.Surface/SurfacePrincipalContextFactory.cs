using System.Collections.Concurrent;
using FabrCore.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;

namespace FabrCore.Surface;

public sealed class SurfacePrincipalContextFactory : ISurfacePrincipalContextFactory, IAsyncDisposable
{
    private readonly IClusterClient clusterClient;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<SurfacePrincipalContextFactory> logger;
    private readonly ConcurrentDictionary<string, CachedContext> contextCache = new();
    private volatile bool disposed;

    public SurfacePrincipalContextFactory(IClusterClient clusterClient, ILoggerFactory loggerFactory)
    {
        this.clusterClient = clusterClient;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<SurfacePrincipalContextFactory>();
    }

    public async Task<ISurfacePrincipalContext> CreateAsync(string handle, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        return await CreateContextInternalAsync(handle, cancellationToken);
    }

    public async Task<ISurfacePrincipalContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        while (true)
        {
            var cachedContext = contextCache.GetOrAdd(handle, h =>
                new CachedContext(new Lazy<Task<SurfacePrincipalContext>>(() => CreateContextInternalAsync(h, cancellationToken))));

            if (!cachedContext.TryAddReference())
            {
                continue;
            }

            try
            {
                var context = await cachedContext.Context.Value;
                if (!context.IsDisposed)
                {
                    return context;
                }

                await ReleaseReferenceAsync(handle, cachedContext, retire: true);
            }
            catch
            {
                await ReleaseReferenceAsync(handle, cachedContext, retire: true);
                throw;
            }
        }
    }

    public async Task<bool> ReleaseAsync(string handle)
    {
        if (disposed)
        {
            return false;
        }

        return await ReleaseInternalAsync(handle);
    }

    private async Task<bool> ReleaseInternalAsync(string handle, bool retire = false)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        if (!contextCache.TryGetValue(handle, out var cachedContext))
        {
            return false;
        }

        return await ReleaseReferenceAsync(handle, cachedContext, retire);
    }

    public bool HasContext(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        if (!contextCache.TryGetValue(handle, out var cachedContext) || !cachedContext.Context.IsValueCreated)
        {
            return false;
        }

        try
        {
            var task = cachedContext.Context.Value;
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
            await ReleaseInternalAsync(handle, retire: true);
        }

        GC.SuppressFinalize(this);
    }

    private async Task<SurfacePrincipalContext> CreateContextInternalAsync(string handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var principalGrain = clusterClient.GetGrain<IPrincipalGrain>(handle);
        var context = SurfacePrincipalContext.CreateUninitialized(
            clusterClient,
            loggerFactory.CreateLogger<SurfacePrincipalContext>(),
            handle,
            principalGrain);

        var observerRef = clusterClient.CreateObjectReference<IPrincipalGrainObserver>(context);
        context.SetObserverReference(observerRef);

        cancellationToken.ThrowIfCancellationRequested();
        await principalGrain.Subscribe(observerRef);

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

    private async Task<bool> ReleaseReferenceAsync(string handle, CachedContext cachedContext, bool retire)
    {
        var shouldDispose = cachedContext.ReleaseReference(retire);
        if (!shouldDispose)
        {
            return true;
        }

        contextCache.TryRemove(new KeyValuePair<string, CachedContext>(handle, cachedContext));

        if (!cachedContext.Context.IsValueCreated)
        {
            return true;
        }

        try
        {
            var context = await cachedContext.Context.Value;
            await context.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error disposing cached Surface context for handle {Handle}.", handle);
        }

        return true;
    }

    private sealed class CachedContext
    {
        private readonly object syncRoot = new();
        private int referenceCount;
        private bool retired;

        public CachedContext(Lazy<Task<SurfacePrincipalContext>> context)
        {
            Context = context;
        }

        public Lazy<Task<SurfacePrincipalContext>> Context { get; }

        public bool TryAddReference()
        {
            lock (syncRoot)
            {
                if (retired)
                {
                    return false;
                }

                referenceCount++;
                return true;
            }
        }

        public bool ReleaseReference(bool retire)
        {
            lock (syncRoot)
            {
                if (retire)
                {
                    retired = true;
                }

                if (referenceCount > 0)
                {
                    referenceCount--;
                }

                if (referenceCount > 0)
                {
                    return false;
                }

                retired = true;
                return true;
            }
        }
    }
}
