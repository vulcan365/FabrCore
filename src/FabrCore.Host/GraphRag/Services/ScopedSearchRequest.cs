namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// Request for a scoped knowledge search. Scope is mandatory — the service
/// refuses any search that does not specify at least one scope. The scope
/// list is treated as a plain inclusion set: every listed scope is searched
/// on equal footing, and results are ranked strictly by raw vector distance.
/// List ordering carries no weight — a scope at position N has the exact
/// same pull on ranking as a scope at position 0. Callers never trust the
/// LLM for scopes; scopes come from the agent's configuration, the HTTP
/// request's authenticated claims, or an equivalent trusted source.
/// </summary>
/// <param name="Query">Free-text search query. Required.</param>
/// <param name="Scopes">
/// List of scope keys the caller is allowed to search. Must contain at least
/// one entry. Order is preserved in the result set for observability but
/// does not influence ranking.
/// </param>
/// <param name="Limit">Maximum results to return (1-200).</param>
/// <param name="EntityTypeFilter">
/// Optional entity type filter (e.g. "Document", "Concept"). Not a security
/// boundary — just a content filter on top of the authoritative scope filter.
/// </param>
/// <param name="DomainFilter">
/// Optional domain name filter. Domains are a taxonomy label, not an access
/// control. Use scope for access control.
/// </param>
public sealed record ScopedSearchRequest(
    string Query,
    IReadOnlyList<string> Scopes,
    int Limit = 10,
    string? EntityTypeFilter = null,
    string? DomainFilter = null)
{
    /// <summary>
    /// Validates the request. Throws <see cref="ArgumentException"/> or
    /// <see cref="ArgumentOutOfRangeException"/> if the contract is violated.
    /// Call this at the top of every service method so the SQL layer never
    /// sees a bad request.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Query))
            throw new ArgumentException("Query is required", nameof(Query));
        if (Scopes is null || Scopes.Count == 0)
            throw new ArgumentException(
                "At least one scope is required. Search without a scope is not permitted.",
                nameof(Scopes));
        if (Scopes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Scope values cannot be null or whitespace", nameof(Scopes));
        if (Limit <= 0 || Limit > 200)
            throw new ArgumentOutOfRangeException(nameof(Limit), "Limit must be between 1 and 200");
    }
}

/// <summary>
/// Request for a scoped relationship traversal. Same scope rules as
/// <see cref="ScopedSearchRequest"/> — every endpoint that shows up in the
/// result set must be in one of the supplied scopes.
/// </summary>
public sealed record ScopedRelationshipRequest(
    string EntityName,
    string EntityType,
    IReadOnlyList<string> Scopes,
    string? RelationshipTypeFilter = null,
    int Depth = 1,
    string? SourceScope = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EntityName))
            throw new ArgumentException("EntityName is required", nameof(EntityName));
        if (string.IsNullOrWhiteSpace(EntityType))
            throw new ArgumentException("EntityType is required", nameof(EntityType));
        if (Scopes is null || Scopes.Count == 0)
            throw new ArgumentException(
                "At least one scope is required. Relationship traversal without a scope is not permitted.",
                nameof(Scopes));
        if (Scopes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Scope values cannot be null or whitespace", nameof(Scopes));
        if (Depth is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(Depth), "Depth must be 1, 2, or 3");
        if (SourceScope is not null && !Scopes.Contains(SourceScope, StringComparer.Ordinal))
            throw new ArgumentException("SourceScope must be one of the allowed Scopes", nameof(SourceScope));
    }
}

