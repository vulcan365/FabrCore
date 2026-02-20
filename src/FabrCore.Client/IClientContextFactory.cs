namespace FabrCore.Client
{
    /// <summary>
    /// Factory for creating and managing IClientContext instances.
    /// Provides thread-safe context management with support for caching and lifecycle control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This factory is designed to be thread-safe and suitable for use in:
    /// - Blazor Server applications (multiple circuits sharing the factory)
    /// - ASP.NET Web API controllers (concurrent requests)
    /// - Any multi-threaded environment
    /// </para>
    /// <para>
    /// Usage Patterns:
    /// - Use CreateAsync for short-lived, request-scoped contexts
    /// - Use GetOrCreateAsync for long-lived, cached contexts (e.g., per-user in Blazor Server)
    /// </para>
    /// </remarks>
    public interface IClientContextFactory
    {
        /// <summary>
        /// Creates a new client context with the specified handle.
        /// The returned context is fully initialized and ready to use.
        /// </summary>
        /// <param name="handle">The unique handle/identifier for this client (typically a user ID).</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A fully initialized client context. The caller is responsible for disposing this context.</returns>
        /// <exception cref="ArgumentNullException">Thrown when handle is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown when initialization fails.</exception>
        /// <remarks>
        /// Each call creates a new context instance. For scenarios where you want to reuse
        /// contexts per handle (e.g., per-user in Blazor Server), use GetOrCreateAsync instead.
        /// </remarks>
        Task<IClientContext> CreateAsync(string handle, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets an existing context for the specified handle, or creates a new one if none exists.
        /// The context is cached and shared across all callers with the same handle.
        /// </summary>
        /// <param name="handle">The unique handle/identifier for this client (typically a user ID).</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A fully initialized client context. This context is managed by the factory and should NOT be disposed by the caller.</returns>
        /// <exception cref="ArgumentNullException">Thrown when handle is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown when initialization fails.</exception>
        /// <remarks>
        /// <para>
        /// This method is ideal for Blazor Server scenarios where multiple components may need
        /// access to the same user's context. The factory manages the lifecycle of cached contexts.
        /// </para>
        /// <para>
        /// Thread Safety: This method is thread-safe. Concurrent calls with the same handle
        /// will receive the same context instance (only one creation occurs).
        /// </para>
        /// </remarks>
        Task<IClientContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases a cached context for the specified handle.
        /// The context will be disposed and removed from the cache.
        /// </summary>
        /// <param name="handle">The handle of the context to release.</param>
        /// <returns>True if a context was found and released; false if no context existed for the handle.</returns>
        /// <remarks>
        /// Use this when a user logs out or a Blazor circuit ends to clean up resources.
        /// This method is safe to call even if no context exists for the handle.
        /// </remarks>
        Task<bool> ReleaseAsync(string handle);

        /// <summary>
        /// Checks if a cached context exists for the specified handle.
        /// </summary>
        /// <param name="handle">The handle to check.</param>
        /// <returns>True if a cached context exists; false otherwise.</returns>
        bool HasContext(string handle);
    }
}
