namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// Authoritative search surface for the GraphRAG knowledge graph. Every
/// search path — LLM tool, agent wrapper, HTTP controller, WebSocket handler —
/// goes through this service. Scope enforcement lives here and nowhere else.
///
/// Every method requires a caller-supplied list of scope keys. The service
/// returns JSON strings (the same shape the plugins used to return) so
/// callers can pass results straight through to an LLM or a client.
///
/// Scope filter semantics:
/// <list type="bullet">
///   <item>Null or empty Scopes → <see cref="ArgumentException"/>.</item>
///   <item>Populated Scopes → results are restricted to rows whose
///         <c>ScopeKey</c> is in the list. All listed scopes are treated
///         on equal footing; ranking is driven purely by raw vector
///         distance. Scope list ordering is informational only.</item>
/// </list>
/// </summary>
public interface IKnowledgeSearchService
{
    /// <summary>
    /// Vector-similarity search over <c>grag.KnowledgeEntity</c> with scope
    /// filtering. Returns a JSON array of matching entities ordered strictly
    /// by raw vector distance, plus Domain/Category provenance.
    /// </summary>
    Task<string> SearchEntitiesAsync(ScopedSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Vector-similarity search over <c>grag.KnowledgeChunk</c> with scope
    /// filtering. Chunks carry a denormalized <c>ScopeKey</c> column so this
    /// is a single-table filter (no join needed just to check scope).
    /// </summary>
    Task<string> SearchChunksAsync(ScopedSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Graph traversal from a named entity. Applies scope filtering to
    /// BOTH endpoints of every edge — this closes the hole in the old
    /// <c>SearchRelationships</c> tool which had no scope filter at all.
    /// </summary>
    Task<string> SearchRelationshipsAsync(ScopedRelationshipRequest request, CancellationToken ct = default);

    /// <summary>
    /// Hybrid search: vector search on entities + chunks (both scope-filtered),
    /// then graph expansion from the discovered entities with scope
    /// enforcement on the traversal endpoints.
    /// </summary>
    Task<string> HybridSearchAsync(
        ScopedSearchRequest request,
        int graphDepth = 1,
        int vectorLimit = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Deep search: vector search on entities + chunks, then a bounded
    /// relationship-expansion loop that deduplicates entities, chunks,
    /// relationships, taxonomy, metadata, and provenance into one structured
    /// evidence object. Scope enforcement is identical to hybrid search.
    /// </summary>
    Task<string> DeepSearchAsync(
        ScopedSearchRequest request,
        int graphDepth = 2,
        int vectorLimit = 10,
        int maxIterations = 2,
        CancellationToken ct = default);
}
