using FabrCore.Services.GraphRag.Models;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// Registry for scope keys. Scope is the GraphRAG access boundary: every
/// entity row carries a single <c>ScopeKey</c>, and every search or
/// traversal is filtered by a caller-supplied list of allowed scope keys.
/// This service owns the <c>grag.KnowledgeScope</c> table.
/// </summary>
public interface IKnowledgeScopeService
{
    /// <summary>
    /// Creates a new scope. Throws if the scope key already exists.
    /// </summary>
    Task<KnowledgeScope> CreateScopeAsync(
        string scopeKey,
        string description,
        double defaultPriority = 1.0,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the scope with the given key, or null if not found.
    /// </summary>
    Task<KnowledgeScope?> GetScopeAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>
    /// Returns every registered scope, ordered by key.
    /// </summary>
    Task<IReadOnlyList<KnowledgeScope>> ListScopesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if a scope with the given key exists.
    /// </summary>
    Task<bool> ScopeExistsAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of entities currently assigned to the given scope.
    /// </summary>
    Task<int> CountEntitiesInScopeAsync(string scopeKey, CancellationToken ct = default);
}
