namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Factory for scope-bound memory service instances.
/// Registered as a singleton in DI. Caches instances per scope key, so agents
/// configured with the same shared scope (e.g. "bank-recon") share one instance,
/// while agents using their own handle stay isolated.
/// </summary>
public interface IAgentMemoryProvider
{
    /// <summary>
    /// Get or create a memory service instance bound to the given scope key —
    /// typically the agent's handle (isolated memory) or a named shared scope.
    /// Thread-safe; the same key always returns the same instance.
    /// </summary>
    /// <param name="scopeKey">The memory scope to bind the service to.</param>
    /// <returns>An <see cref="IAgentMemoryService"/> bound to the scope.</returns>
    IAgentMemoryService GetMemoryService(string scopeKey);

    /// <summary>
    /// Remove the cached service instance for a scope (used after a scope is deleted
    /// so stale per-instance state is not reused). Returns true when an instance was cached.
    /// </summary>
    bool EvictMemoryService(string scopeKey);
}
