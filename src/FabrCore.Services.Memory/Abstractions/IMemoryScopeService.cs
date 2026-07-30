using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Registry of memory scopes (<c>mem.MemoryScope</c>). Shared scopes are created
/// explicitly (by admins or host code); agent-handle scopes are auto-registered
/// on first write so they show up in administration tooling.
/// </summary>
public interface IMemoryScopeService
{
    /// <summary>
    /// Create a new scope. Throws <see cref="InvalidOperationException"/> when the
    /// scope key already exists. Writes a <c>ScopeCreated</c> audit entry.
    /// </summary>
    Task<MemoryScope> CreateScopeAsync(
        string scopeKey, string? description, bool isShared = true,
        string? createdBy = null, CancellationToken ct = default);

    /// <summary>
    /// Idempotently register a scope (MERGE). Used to auto-register agent-handle
    /// scopes on first write. Never overwrites an existing row.
    /// </summary>
    Task EnsureScopeAsync(string scopeKey, bool isShared = false, CancellationToken ct = default);

    /// <summary>Get a scope by key, or null when not registered.</summary>
    Task<MemoryScope?> GetScopeAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>List all registered scopes.</summary>
    Task<IReadOnlyList<MemoryScope>> ListScopesAsync(CancellationToken ct = default);

    /// <summary>Whether a scope row exists.</summary>
    Task<bool> ScopeExistsAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>Count memory entities in a scope (excludes the internal index sentinel).</summary>
    Task<int> CountMemoriesInScopeAsync(string scopeKey, CancellationToken ct = default);
}
