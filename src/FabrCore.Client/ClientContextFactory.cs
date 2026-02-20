using Fabr.Core;
using Fabr.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Trace;

namespace Fabr.Client
{
    /// <summary>
    /// Thread-safe factory for creating and managing ClientContext instances.
    /// Supports both per-request contexts and cached per-handle contexts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This factory is designed to be thread-safe and registered as a singleton.
    /// It is suitable for use in:
    /// - Blazor Server applications (multiple circuits sharing the factory)
    /// - ASP.NET Web API controllers (concurrent requests)
    /// - Any multi-threaded environment
    /// </para>
    /// <para>
    /// Usage Patterns:
    /// - CreateAsync: Creates a new context each time. Caller is responsible for disposal.
    /// - GetOrCreateAsync: Returns cached context or creates new one. Factory manages lifecycle.
    /// </para>
    /// </remarks>
    public sealed class ClientContextFactory : IClientContextFactory, IAsyncDisposable
    {
        private static readonly ActivitySource ActivitySource = new("Fabr.Client.ClientContextFactory");
        private static readonly Meter Meter = new("Fabr.Client.ClientContextFactory");

        // Metrics
        private static readonly Counter<long> ContextsCreatedCounter = Meter.CreateCounter<long>(
            "fabr.client.factory.contexts.created",
            description: "Number of client contexts created by factory");

        private static readonly Counter<long> ContextsCachedCounter = Meter.CreateCounter<long>(
            "fabr.client.factory.contexts.cached",
            description: "Number of client contexts retrieved from cache");

        private static readonly Counter<long> ContextsReleasedCounter = Meter.CreateCounter<long>(
            "fabr.client.factory.contexts.released",
            description: "Number of client contexts released from cache");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabr.client.factory.errors",
            description: "Number of errors encountered in factory");

        private static readonly Histogram<double> ContextCreationDuration = Meter.CreateHistogram<double>(
            "fabr.client.factory.creation.duration",
            unit: "ms",
            description: "Duration of client context creation");

        private readonly IClusterClient _clusterClient;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ClientContextFactory> _logger;

        // Thread-safe cache for contexts. Uses Lazy to ensure only one context is created per handle
        // even with concurrent GetOrCreateAsync calls.
        private readonly ConcurrentDictionary<string, Lazy<Task<ClientContext>>> _contextCache = new();

        // Lock for cleanup operations
        private readonly SemaphoreSlim _cleanupLock = new(1, 1);

        private volatile bool _disposed;

        public ClientContextFactory(
            IClusterClient clusterClient,
            ILoggerFactory loggerFactory)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = loggerFactory.CreateLogger<ClientContextFactory>();

            _logger.LogDebug("ClientContextFactory created");
        }

        /// <inheritdoc/>
        public async Task<IClientContext> CreateAsync(string handle, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidateHandle(handle);

            using var activity = ActivitySource.StartActivity("CreateAsync", ActivityKind.Internal);
            activity?.SetTag("client.handle", handle);

            _logger.LogInformation("Creating new client context for handle: {Handle}", handle);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var context = await CreateContextInternalAsync(handle, cancellationToken);

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                ContextCreationDuration.Record(elapsed,
                    new KeyValuePair<string, object?>("client.handle", handle),
                    new KeyValuePair<string, object?>("cached", false));

                ContextsCreatedCounter.Add(1,
                    new KeyValuePair<string, object?>("client.handle", handle),
                    new KeyValuePair<string, object?>("cached", false));

                _logger.LogInformation("Client context created successfully for handle: {Handle} in {Duration}ms", handle, elapsed);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return context;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create client context for handle: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "context_creation_failed"),
                    new KeyValuePair<string, object?>("client.handle", handle));
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<IClientContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidateHandle(handle);

            using var activity = ActivitySource.StartActivity("GetOrCreateAsync", ActivityKind.Internal);
            activity?.SetTag("client.handle", handle);

            var startTime = Stopwatch.GetTimestamp();

            try
            {
                // Use GetOrAdd with Lazy<Task<>> to ensure only one creation happens per handle
                var lazyContext = _contextCache.GetOrAdd(handle, h =>
                    new Lazy<Task<ClientContext>>(() => CreateContextInternalAsync(h, cancellationToken)));

                bool wasCreated = !lazyContext.IsValueCreated;
                var context = await lazyContext.Value;

                // Check if the cached context was disposed (shouldn't happen normally, but be defensive)
                if (context.IsDisposed)
                {
                    _logger.LogWarning("Cached context for handle {Handle} was disposed. Creating new one.", handle);

                    // Remove the disposed context and try again
                    _contextCache.TryRemove(handle, out _);
                    return await GetOrCreateAsync(handle, cancellationToken);
                }

                var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;

                if (wasCreated)
                {
                    ContextCreationDuration.Record(elapsed,
                        new KeyValuePair<string, object?>("client.handle", handle),
                        new KeyValuePair<string, object?>("cached", true));

                    ContextsCreatedCounter.Add(1,
                        new KeyValuePair<string, object?>("client.handle", handle),
                        new KeyValuePair<string, object?>("cached", true));

                    _logger.LogInformation("Client context created and cached for handle: {Handle} in {Duration}ms", handle, elapsed);
                }
                else
                {
                    ContextsCachedCounter.Add(1,
                        new KeyValuePair<string, object?>("client.handle", handle));

                    _logger.LogDebug("Returning cached client context for handle: {Handle}", handle);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.SetTag("cached", !wasCreated);

                return context;
            }
            catch (Exception ex)
            {
                // If creation failed, remove the failed Lazy from the cache
                _contextCache.TryRemove(handle, out _);

                _logger.LogError(ex, "Failed to get or create client context for handle: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "get_or_create_failed"),
                    new KeyValuePair<string, object?>("client.handle", handle));
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> ReleaseAsync(string handle)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(handle))
            {
                return false;
            }

            using var activity = ActivitySource.StartActivity("ReleaseAsync", ActivityKind.Internal);
            activity?.SetTag("client.handle", handle);

            _logger.LogInformation("Releasing cached context for handle: {Handle}", handle);

            try
            {
                if (_contextCache.TryRemove(handle, out var lazyContext))
                {
                    // Only dispose if the context was actually created
                    if (lazyContext.IsValueCreated)
                    {
                        try
                        {
                            var context = await lazyContext.Value;
                            await context.DisposeAsync();
                        }
                        catch (Exception ex)
                        {
                            // Log but don't throw - context might have been disposed already
                            _logger.LogWarning(ex, "Error disposing context during release for handle: {Handle}", handle);
                        }
                    }

                    ContextsReleasedCounter.Add(1,
                        new KeyValuePair<string, object?>("client.handle", handle));

                    _logger.LogInformation("Cached context released for handle: {Handle}", handle);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return true;
                }

                _logger.LogDebug("No cached context found for handle: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error releasing context for handle: {Handle}", handle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "release_failed"),
                    new KeyValuePair<string, object?>("client.handle", handle));
                throw;
            }
        }

        /// <inheritdoc/>
        public bool HasContext(string handle)
        {
            if (string.IsNullOrWhiteSpace(handle))
            {
                return false;
            }

            if (_contextCache.TryGetValue(handle, out var lazyContext))
            {
                // Only return true if the context was created and is not disposed
                if (lazyContext.IsValueCreated)
                {
                    try
                    {
                        var task = lazyContext.Value;
                        if (task.IsCompletedSuccessfully && !task.Result.IsDisposed)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Context creation failed, so it doesn't really exist
                        return false;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Disposes the factory and all cached contexts.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            using var activity = ActivitySource.StartActivity("DisposeAsync", ActivityKind.Internal);

            _logger.LogInformation("Disposing ClientContextFactory with {Count} cached contexts", _contextCache.Count);

            await _cleanupLock.WaitAsync();
            try
            {
                // Dispose all cached contexts
                var handles = _contextCache.Keys.ToList();
                foreach (var handle in handles)
                {
                    if (_contextCache.TryRemove(handle, out var lazyContext) && lazyContext.IsValueCreated)
                    {
                        try
                        {
                            var context = await lazyContext.Value;
                            await context.DisposeAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error disposing cached context for handle: {Handle}", handle);
                        }
                    }
                }

                _logger.LogInformation("ClientContextFactory disposed successfully");
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            finally
            {
                _cleanupLock.Release();
                _cleanupLock.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Creates a new ClientContext with full initialization.
        /// </summary>
        private async Task<ClientContext> CreateContextInternalAsync(string handle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var logger = _loggerFactory.CreateLogger<ClientContext>();

            // Get the grain with the specified handle as primary key
            var clientGrain = _clusterClient.GetGrain<IClientGrain>(handle);
            _logger.LogDebug("Client grain obtained for handle: {Handle}", handle);

            // Create the context first (we need it to create the observer reference)
            // Pass null for observerRef initially - we'll set it after creating the reference
            var context = ClientContext.CreateUninitialized(_clusterClient, logger, handle, clientGrain);

            // Create observer reference pointing to the context
            var observerRef = _clusterClient.CreateObjectReference<IClientGrainObserver>(context);

            // Now initialize the context with the observer reference
            context.SetObserverReference(observerRef);

            cancellationToken.ThrowIfCancellationRequested();

            // Subscribe to the grain
            await clientGrain.Subscribe(observerRef);
            _logger.LogDebug("Subscribed to client grain for handle: {Handle}", handle);

            // Record the connection
            context.RecordConnected();

            return context;
        }

        private void ValidateHandle(string handle)
        {
            if (string.IsNullOrWhiteSpace(handle))
            {
                throw new ArgumentNullException(nameof(handle), "Handle cannot be null or empty");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
